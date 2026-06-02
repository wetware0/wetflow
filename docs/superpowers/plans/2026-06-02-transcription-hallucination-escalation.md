# Transcription Hallucination Escalation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect bracket/low-confidence Whisper segments and re-transcribe just that audio span with a larger local model, splicing the better result back in (or dropping it when the audio holds no recoverable speech).

**Architecture:** A new pure-logic `SegmentResolver` classifies each raw Whisper segment (Clean / Blank / Flagged) and, for flagged ones, calls an injected escalation delegate over a sliced audio span, deciding whether to replace or drop. `Transcriber` owns the Whisper factories (primary + lazily-loaded escalation), WAV slicing, and wires the real escalation processor into the resolver. Escalation is offline-only and opt-out via the `EscalationModel` setting.

**Tech Stack:** C# / .NET 8 (net8.0-windows), Whisper.net 1.8.1, xunit.

**Spec:** `docs/superpowers/specs/2026-06-02-transcription-hallucination-escalation-design.md`

---

## File Structure

- **Create** `src/SegmentResolver.cs` — pure logic: `Classify`, `ComputeSpan`, `ResolveAsync`, and the `RawSegment` / `EscalationResult` / `SegmentClass` types. No model or IO dependencies.
- **Create** `tests/SegmentResolverTests.cs` — unit tests for all `SegmentResolver` logic.
- **Modify** `src/AppSettings.cs` — add `EscalationModel` property.
- **Modify** `src/Transcriber.cs` — capture min token probability, return raw segments from `TranscribeStreamAsync`, add `ReadPcm` / `TranscribeChunkAsync` / `CreateFactoryAsync` / escalation factory management, revert the inert builder options, wire in `SegmentResolver`.
- **Modify** `src/TrayApp.cs` — pass `_settings.EscalationModel` into `TranscribeAsync`.
- **Modify** `tests/AppSettingsTests.cs` — add a default-value test for `EscalationModel`.

---

## Task 1: Add `EscalationModel` setting

**Files:**
- Modify: `src/AppSettings.cs:25`
- Test: `tests/AppSettingsTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/AppSettingsTests.cs` (inside the existing `AppSettingsTests` class — if for any reason the file/class is absent, create it with `using WetFlow; using Xunit; namespace WetFlow.Tests;` and a `public class AppSettingsTests`):

```csharp
    [Fact]
    public void Default_EscalationModel_IsSmall()
    {
        Assert.Equal("small", new AppSettings().EscalationModel);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~Default_EscalationModel_IsSmall"`
Expected: FAIL — `AppSettings` has no `EscalationModel` property (compile error).

- [ ] **Step 3: Add the property**

In `src/AppSettings.cs`, immediately after the `WhisperModel` line (line 17), add:

```csharp
    public string EscalationModel { get; set; } = "small"; // "" disables hallucination escalation
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~Default_EscalationModel_IsSmall"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/AppSettings.cs tests/AppSettingsTests.cs
git commit -m "feat: add EscalationModel setting (default small)"
```

---

## Task 2: `SegmentResolver.Classify` + types

**Files:**
- Create: `src/SegmentResolver.cs`
- Test: `tests/SegmentResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/SegmentResolverTests.cs`:

```csharp
using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class SegmentResolverClassifyTests
{
    [Fact]
    public void PureAnnotation_IsFlagged()
        => Assert.Equal(SegmentResolver.SegmentClass.Flagged,
            SegmentResolver.Classify("[Music]", "", 0.9f));

    [Fact]
    public void BlankAudio_IsBlank()
        => Assert.Equal(SegmentResolver.SegmentClass.Blank,
            SegmentResolver.Classify("[BLANK_AUDIO]", "", 0.9f));

    [Fact]
    public void HighConfidenceSpeech_IsClean()
        => Assert.Equal(SegmentResolver.SegmentClass.Clean,
            SegmentResolver.Classify("Hello world", "Hello world", 0.9f));

    [Fact]
    public void HighConfidenceMixed_IsClean()
        => Assert.Equal(SegmentResolver.SegmentClass.Clean,
            SegmentResolver.Classify("Hello [Music] world", "Hello world", 0.9f));

    [Fact]
    public void LowConfidenceSpeech_IsFlagged()
        => Assert.Equal(SegmentResolver.SegmentClass.Flagged,
            SegmentResolver.Classify("I am very happy to see you again", "I am very happy to see you again", 0.01f));

    [Fact]
    public void ThresholdBoundary_AtThreshold_IsClean()
        => Assert.Equal(SegmentResolver.SegmentClass.Clean,
            SegmentResolver.Classify("words", "words", SegmentResolver.LowConfidenceThreshold));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverClassifyTests"`
Expected: FAIL — `SegmentResolver` does not exist (compile error).

- [ ] **Step 3: Create `SegmentResolver` with types and `Classify`**

Create `src/SegmentResolver.cs`:

```csharp
namespace WetFlow;

// Pure logic that turns raw Whisper segments into the final segment list,
// escalating likely-hallucinated segments through an injected delegate.
internal static class SegmentResolver
{
    // Minimum per-token probability below which a segment with real text is
    // treated as a likely hallucination and escalated. Calibrated from a single
    // sample (hallucination 0.010 vs real-speech floor 0.119) — recalibrate
    // against real flagged recordings.
    internal const float LowConfidenceThreshold = 0.05f;

    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;

    internal enum SegmentClass { Clean, Blank, Flagged }

    // A raw Whisper segment before resolution. MinTokenProb is the minimum
    // probability across the segment's non-special tokens.
    internal readonly record struct RawSegment(string RawText, TimeSpan Start, TimeSpan End, float MinTokenProb);

    // The outcome of escalating one span: the re-transcribed text and its
    // minimum token probability.
    internal readonly record struct EscalationResult(string Text, float MinTokenProb);

    internal static bool IsBlankAudio(string rawText) =>
        rawText.Contains("[BLANK_AUDIO]", StringComparison.Ordinal);

    // cleanedText is rawText with annotation tokens stripped (Transcriber.FilterAnnotations).
    internal static SegmentClass Classify(string rawText, string cleanedText, float minTokenProb)
    {
        if (cleanedText.Length == 0)
            return IsBlankAudio(rawText) ? SegmentClass.Blank : SegmentClass.Flagged;

        return minTokenProb < LowConfidenceThreshold ? SegmentClass.Flagged : SegmentClass.Clean;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverClassifyTests"`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/SegmentResolver.cs tests/SegmentResolverTests.cs
git commit -m "feat: add SegmentResolver classification"
```

---

## Task 3: `SegmentResolver.ComputeSpan` (audio slice math)

**Files:**
- Modify: `src/SegmentResolver.cs`
- Test: `tests/SegmentResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/SegmentResolverTests.cs`:

```csharp
public class SegmentResolverSpanTests
{
    // 16 kHz * 2 bytes = 32000 bytes per second.

    [Fact]
    public void Span_LongerThanOneSecond_NotExpanded()
    {
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), pcmLength: 1_000_000);
        Assert.Equal(64000, start);
        Assert.Equal(32000, length);
    }

    [Fact]
    public void Span_ShorterThanOneSecond_ExpandedSymmetrically()
    {
        // 2.0s..2.2s = 6400 bytes -> expand to 32000, centred.
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.2), pcmLength: 1_000_000);
        Assert.Equal(51200, start);
        Assert.Equal(32000, length);
    }

    [Fact]
    public void Span_NearStart_ClampsToZero()
    {
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(0.2), pcmLength: 32000);
        Assert.Equal(0, start);
        Assert.Equal(19200, length); // expansion clamped at both ends
    }

    [Fact]
    public void Span_ByteOffsetsAreSampleAligned()
    {
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(1.0000625), TimeSpan.FromSeconds(3), pcmLength: 1_000_000);
        Assert.Equal(0, start % 2);
        Assert.Equal(0, length % 2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverSpanTests"`
Expected: FAIL — `ComputeSpan` not defined (compile error).

- [ ] **Step 3: Add `ComputeSpan`**

In `src/SegmentResolver.cs`, add inside the class after `Classify`:

```csharp
    // Byte range within the chunk PCM for [start,end], expanded to at least one
    // second (so Whisper has a usable window) and clamped to [0, pcmLength].
    internal static (int Start, int Length) ComputeSpan(TimeSpan start, TimeSpan end, int pcmLength)
    {
        int bytesPerSecond = SampleRate * BytesPerSample;
        int startByte = (int)(start.TotalSeconds * bytesPerSecond);
        int endByte = (int)(end.TotalSeconds * bytesPerSecond);

        startByte -= startByte % BytesPerSample;
        endByte -= endByte % BytesPerSample;

        int minBytes = bytesPerSecond; // one second
        if (endByte - startByte < minBytes)
        {
            int deficit = minBytes - (endByte - startByte);
            startByte -= deficit / 2;
            endByte += deficit - deficit / 2;
        }

        startByte = Math.Clamp(startByte, 0, pcmLength);
        endByte = Math.Clamp(endByte, startByte, pcmLength);
        return (startByte, endByte - startByte);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverSpanTests"`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/SegmentResolver.cs tests/SegmentResolverTests.cs
git commit -m "feat: add SegmentResolver.ComputeSpan slice math"
```

---

## Task 4: `SegmentResolver.ResolveAsync` (classify → escalate → merge)

**Files:**
- Modify: `src/SegmentResolver.cs`
- Test: `tests/SegmentResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/SegmentResolverTests.cs`:

```csharp
public class SegmentResolverResolveTests
{
    private static SegmentResolver.RawSegment Raw(string text, double startSecs, double endSecs, float minProb)
        => new(text, TimeSpan.FromSeconds(startSecs), TimeSpan.FromSeconds(endSecs), minProb);

    // Identity filter stand-in for Transcriber.FilterAnnotations in tests that don't need stripping.
    private static string Strip(string s) => WetFlow.Transcriber.FilterAnnotations(s);

    private static Func<(int, int), CancellationToken, Task<SegmentResolver.EscalationResult>> Escalator(
        string text, float minProb)
        => (_, _) => Task.FromResult(new SegmentResolver.EscalationResult(text, minProb));

    [Fact]
    public async Task Clean_PassesThrough()
    {
        var segs = new[] { Raw("Hello world", 0, 1, 0.9f) };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("X", 0.9f), default);
        Assert.Single(result);
        Assert.Equal("Hello world", result[0].Text);
    }

    [Fact]
    public async Task Blank_IsDropped()
    {
        var segs = new[] { Raw("[BLANK_AUDIO]", 0, 1, 0.9f) };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("X", 0.9f), default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Flagged_ConfidentEscalation_IsReplaced()
    {
        var segs = new[] { Raw("I am very happy to see you again", 0, 2, 0.01f) };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("the real words", 0.8f), default);
        Assert.Single(result);
        Assert.Equal("the real words", result[0].Text);
    }

    [Fact]
    public async Task Flagged_EmptyEscalation_IsDropped()
    {
        var segs = new[] { Raw("[Music]", 0, 2, 0.9f) };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("", 0.9f), default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Flagged_LowConfidenceEscalation_IsDropped()
    {
        var segs = new[] { Raw("I am very happy to see you again", 0, 2, 0.01f) };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("more noise", 0.01f), default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Flagged_EscalatorThrows_KeepsCleanedText()
    {
        var segs = new[] { Raw("I am very happy to see you again", 0, 2, 0.01f) };
        Func<(int, int), CancellationToken, Task<SegmentResolver.EscalationResult>> throwing =
            (_, _) => throw new InvalidOperationException("unavailable");
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, throwing, default);
        Assert.Single(result);
        Assert.Equal("I am very happy to see you again", result[0].Text);
    }

    [Fact]
    public async Task Flagged_PureAnnotation_EscalatorThrows_IsDropped()
    {
        var segs = new[] { Raw("[Music]", 0, 2, 0.9f) };
        Func<(int, int), CancellationToken, Task<SegmentResolver.EscalationResult>> throwing =
            (_, _) => throw new InvalidOperationException("unavailable");
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, throwing, default);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverResolveTests"`
Expected: FAIL — `ResolveAsync` not defined (compile error).

- [ ] **Step 3: Add `ResolveAsync`**

In `src/SegmentResolver.cs`, add inside the class after `ComputeSpan`:

```csharp
    // Walks raw segments: keeps Clean (stripped) text, drops Blank, and for
    // Flagged segments escalates the sliced span — replacing with confident
    // re-transcribed text, or dropping when nothing is recoverable. On an
    // escalation error the cleaned original is kept rather than lost.
    internal static async Task<List<(string Text, TimeSpan Start, TimeSpan End)>> ResolveAsync(
        IReadOnlyList<RawSegment> rawSegments,
        int pcmLength,
        Func<string, string> filter,
        Func<(int Start, int Length), CancellationToken, Task<EscalationResult>> escalate,
        CancellationToken ct)
    {
        var result = new List<(string, TimeSpan, TimeSpan)>();
        foreach (var seg in rawSegments)
        {
            ct.ThrowIfCancellationRequested();
            var cleaned = filter(seg.RawText);

            switch (Classify(seg.RawText, cleaned, seg.MinTokenProb))
            {
                case SegmentClass.Blank:
                    continue;

                case SegmentClass.Clean:
                    result.Add((cleaned, seg.Start, seg.End));
                    break;

                case SegmentClass.Flagged:
                    EscalationResult esc;
                    try
                    {
                        esc = await escalate(ComputeSpan(seg.Start, seg.End, pcmLength), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Escalation unavailable/errored — keep cleaned text rather than lose possibly-real speech.
                        if (cleaned.Length > 0) result.Add((cleaned, seg.Start, seg.End));
                        continue;
                    }

                    var escCleaned = filter(esc.Text);
                    if (escCleaned.Length > 0 && esc.MinTokenProb >= LowConfidenceThreshold)
                        result.Add((escCleaned, seg.Start, seg.End)); // Case B — recovered
                    // else drop — Case A, nothing recoverable
                    break;
            }
        }
        return result;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WetFlow.Tests.csproj --filter "FullyQualifiedName~SegmentResolverResolveTests"`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/SegmentResolver.cs tests/SegmentResolverTests.cs
git commit -m "feat: add SegmentResolver.ResolveAsync escalate/merge logic"
```

---

## Task 5: Wire escalation into `Transcriber`

This task changes native/IO code that the project does not unit-test. The gate is: **the project builds, all existing tests plus the new `SegmentResolver` tests pass.** End-to-end behaviour is verified in Task 7.

**Files:**
- Modify: `src/Transcriber.cs`

- [ ] **Step 1: Revert the inert builder options**

In `src/Transcriber.cs`, replace the processor-creation block (the `factory.CreateBuilder()...Build()` call with `WithNoContext()` / `WithNoSpeechThreshold(0.3f)`) with:

```csharp
        // Create a fresh processor per transcription so the model's inference
        // context window does not carry over between recordings.
        await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
```

- [ ] **Step 2: Add the `escalationModel` parameter to `TranscribeAsync`**

Change the `TranscribeAsync` signature to:

```csharp
    public async Task<string> TranscribeAsync(string wavPath, string modelName = "base",
        double shortPauseSecs = 0.5, double longPauseSecs = 1.5,
        bool useGpu = false, string escalationModel = "", CancellationToken cancellationToken = default)
```

- [ ] **Step 3: Replace both transcription paths to go through `TranscribeChunkAsync`**

In `TranscribeAsync`, replace the single-file branch:

```csharp
        if (chunks.Count == 1 && chunks[0].ChunkPath == wavPath)
        {
            using var fileStream = File.OpenRead(wavPath);
            var segments = await TranscribeStreamAsync(processor, fileStream, cancellationToken);
            return FormatSegments(segments, shortPauseSecs, longPauseSecs);
        }
```

with:

```csharp
        if (chunks.Count == 1 && chunks[0].ChunkPath == wavPath)
        {
            var segments = await TranscribeChunkAsync(wavPath, processor, useGpu, escalationModel, cancellationToken);
            return FormatSegments(segments, shortPauseSecs, longPauseSecs);
        }
```

Then, inside the multi-chunk `foreach`, replace:

```csharp
                using var fs = File.OpenRead(chunkPath);
                var segs = await TranscribeStreamAsync(processor, fs, cancellationToken);
                var text = FormatSegments(segs, shortPauseSecs, longPauseSecs).Trim();
```

with:

```csharp
                var segs = await TranscribeChunkAsync(chunkPath, processor, useGpu, escalationModel, cancellationToken);
                var text = FormatSegments(segs, shortPauseSecs, longPauseSecs).Trim();
```

- [ ] **Step 4: Rewrite `TranscribeStreamAsync` to return raw segments + add `MinTokenProb`**

Replace the entire `TranscribeStreamAsync` method with:

```csharp
    private static async Task<List<SegmentResolver.RawSegment>> TranscribeStreamAsync(
        WhisperProcessor processor, Stream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var segs = new List<SegmentResolver.RawSegment>();
        await foreach (var seg in processor.ProcessAsync(stream).WithCancellation(ct))
            segs.Add(new SegmentResolver.RawSegment(seg.Text, seg.Start, seg.End, MinTokenProb(seg)));
        TrayApp.Log($"[TIMING] chunk-transcription: {sw.ElapsedMilliseconds} ms");
        return segs;
    }

    // Minimum probability across the segment's non-special tokens (special tokens
    // such as timestamps render as text beginning with "[_"). Returns 1.0 when the
    // segment has no scorable tokens.
    private static float MinTokenProb(SegmentData seg)
    {
        float min = 1f;
        bool any = false;
        if (seg.Tokens != null)
            foreach (var t in seg.Tokens)
            {
                if (string.IsNullOrEmpty(t.Text) || t.Text.StartsWith("[_", StringComparison.Ordinal)) continue;
                any = true;
                if (t.Probability < min) min = t.Probability;
            }
        return any ? min : 1f;
    }
```

- [ ] **Step 5: Add `ReadPcm` and `TranscribeChunkAsync`**

In `src/Transcriber.cs`, add these methods (e.g. immediately after `TranscribeStreamAsync`):

```csharp
    private static byte[] ReadPcm(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        fs.Seek(44, SeekOrigin.Begin);
        var pcm = new byte[fs.Length - 44];
        _ = fs.Read(pcm, 0, pcm.Length);
        return pcm;
    }

    // Transcribes one chunk and resolves hallucinated segments by escalating the
    // flagged audio span to the larger model.
    private async Task<List<(string Text, TimeSpan Start, TimeSpan End)>> TranscribeChunkAsync(
        string chunkPath, WhisperProcessor processor, bool useGpu, string escalationModel, CancellationToken ct)
    {
        var pcm = ReadPcm(chunkPath);

        List<SegmentResolver.RawSegment> raw;
        using (var fs = File.OpenRead(chunkPath))
            raw = await TranscribeStreamAsync(processor, fs, ct);

        async Task<SegmentResolver.EscalationResult> Escalate((int Start, int Length) span, CancellationToken token)
        {
            var escFactory = await EnsureEscalationFactoryAsync(escalationModel, useGpu);
            if (escFactory == null) throw new InvalidOperationException("escalation unavailable");

            var slicePath = Path.Combine(Path.GetTempPath(), $"wetflow_e_{Guid.NewGuid():N}.wav");
            try
            {
                WritePcmWav(slicePath, pcm, span.Start, span.Length, 16000);
                await using var escProcessor = escFactory.CreateBuilder().WithLanguage("auto").Build();
                using var fs = File.OpenRead(slicePath);
                var escRaw = await TranscribeStreamAsync(escProcessor, fs, token);
                var text = string.Join(" ", escRaw.Select(r => r.RawText));
                float minProb = escRaw.Count > 0 ? escRaw.Min(r => r.MinTokenProb) : 1f;
                return new SegmentResolver.EscalationResult(text, minProb);
            }
            finally
            {
                try { File.Delete(slicePath); } catch { }
            }
        }

        return await SegmentResolver.ResolveAsync(raw, pcm.Length, FilterAnnotations, Escalate, ct);
    }
```

Do **not** add a `using System.Linq;` directive — LINQ (`Select`/`Min`) is already available in this file (it is used by `BuildChunks`), and an explicit duplicate would emit a warning and break the warning-clean build.

- [ ] **Step 6: Refactor factory loading into `CreateFactoryAsync` and reuse it in `EnsureInitializedAsync`**

Replace the body of `EnsureInitializedAsync` (everything inside the `try { ... }` after acquiring the lock) so it delegates to a shared helper, and add the helper. Replace the existing `EnsureInitializedAsync` method with:

```csharp
    private async Task EnsureInitializedAsync(string modelName, bool useGpu)
    {
        if (_currentModelName == modelName && _currentUseGpu == useGpu && _factory != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_currentModelName == modelName && _currentUseGpu == useGpu && _factory != null) return;

            _factory?.Dispose();
            _factory = null;
            _factory = await CreateFactoryAsync(modelName, useGpu);
            _currentModelName = modelName;
            _currentUseGpu = useGpu;
            StatusChanged?.Invoke("Ready");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Downloads the model if needed and builds a factory, preferring GPU and
    // falling back to CPU on failure.
    private async Task<WhisperFactory> CreateFactoryAsync(string modelName, bool useGpu)
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wetflow", "models");
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, $"ggml-{modelName}.bin");

        if (!File.Exists(modelPath))
        {
            StatusChanged?.Invoke($"Downloading Whisper {modelName} model (first run)…");
            var (modelType, quantType) = ParseModel(modelName);
            using var httpClient = new System.Net.Http.HttpClient();
            var downloader = new WhisperGgmlDownloader(httpClient);
            var modelStream = await downloader.GetGgmlModelAsync(modelType, quantType);
            using var fs = File.Create(modelPath);
            await modelStream.CopyToAsync(fs);
        }

        if (useGpu)
        {
            try { return WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true }); }
            catch (Exception ex)
            {
                TrayApp.Log($"GPU init failed, falling back to CPU: {ex}");
                StatusChanged?.Invoke("GPU unavailable — using CPU");
            }
        }

        return WhisperFactory.FromPath(modelPath);
    }
```

- [ ] **Step 7: Add escalation factory fields and `EnsureEscalationFactoryAsync`**

Add fields next to the existing `_factory` fields (near the top of the class, after line 14):

```csharp
    private WhisperFactory? _escalationFactory;
    private string? _currentEscalationModel;
    private bool _currentEscalationUseGpu;
    private bool _escalationLoadFailed;
    private readonly SemaphoreSlim _escalationInitLock = new(1, 1);
```

Add the method (e.g. after `CreateFactoryAsync`):

```csharp
    // Lazily loads the escalation model. Returns null when escalation is disabled
    // (empty model name) or a previous load failed this session.
    private async Task<WhisperFactory?> EnsureEscalationFactoryAsync(string escalationModel, bool useGpu)
    {
        if (string.IsNullOrWhiteSpace(escalationModel) || _escalationLoadFailed) return null;
        if (_currentEscalationModel == escalationModel && _currentEscalationUseGpu == useGpu && _escalationFactory != null)
            return _escalationFactory;

        await _escalationInitLock.WaitAsync();
        try
        {
            if (_currentEscalationModel == escalationModel && _currentEscalationUseGpu == useGpu && _escalationFactory != null)
                return _escalationFactory;

            _escalationFactory?.Dispose();
            _escalationFactory = null;
            try
            {
                _escalationFactory = await CreateFactoryAsync(escalationModel, useGpu);
                _currentEscalationModel = escalationModel;
                _currentEscalationUseGpu = useGpu;
            }
            catch (Exception ex)
            {
                TrayApp.Log($"Escalation model load failed ({escalationModel}); disabling escalation: {ex}");
                _escalationLoadFailed = true;
                _escalationFactory = null;
            }
            return _escalationFactory;
        }
        finally
        {
            _escalationInitLock.Release();
        }
    }
```

- [ ] **Step 8: Dispose the escalation factory**

Replace the `Dispose` method with:

```csharp
    public void Dispose()
    {
        _factory?.Dispose();
        _escalationFactory?.Dispose();
        _initLock.Dispose();
        _escalationInitLock.Dispose();
    }
```

- [ ] **Step 9: Build and run the full test suite**

Run: `dotnet build src/WetFlow.csproj -c Release`
Expected: Build succeeded, 0 errors.

Run: `dotnet test tests/WetFlow.Tests.csproj`
Expected: PASS — all existing tests plus the new `SegmentResolver` tests (clipboard test in `TextInjectorTests` is occasionally flaky; re-run once if only that fails).

- [ ] **Step 10: Commit**

```bash
git add src/Transcriber.cs
git commit -m "feat: escalate flagged segments to larger model in Transcriber"
```

---

## Task 6: Pass the setting from `TrayApp`

**Files:**
- Modify: `src/TrayApp.cs:202-203`

- [ ] **Step 1: Update the `TranscribeAsync` call**

In `src/TrayApp.cs`, replace:

```csharp
                text = await _transcriber.TranscribeAsync(wavPath, _settings.WhisperModel,
                    _settings.ShortPauseSecs, _settings.LongPauseSecs, _settings.UseGpu, token);
```

with:

```csharp
                text = await _transcriber.TranscribeAsync(wavPath, _settings.WhisperModel,
                    _settings.ShortPauseSecs, _settings.LongPauseSecs, _settings.UseGpu,
                    _settings.EscalationModel, token);
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build src/WetFlow.csproj -c Release`
Expected: Build succeeded, 0 errors.

Run: `dotnet test tests/WetFlow.Tests.csproj`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/TrayApp.cs
git commit -m "feat: pass EscalationModel setting into transcription"
```

---

## Task 7: End-to-end verification (manual)

**Files:** none (verification only).

- [ ] **Step 1: Confirm the disabled path is unchanged**

Reasoning check (no code change): with `EscalationModel = ""`, `EnsureEscalationFactoryAsync` returns null, every Flagged segment's escalation throws, and the resolver keeps cleaned text / drops empties — i.e. exactly PR #37 behaviour. Confirm by reading `ResolveAsync` against this case.

- [ ] **Step 2: Run the standalone A/B harness on the synthetic + retained clips**

Build the app, then transcribe (a) the synthetic low-noise hallucination clip and (b) the retained `%APPDATA%\wetflow\failed-audio\*.wav` recordings with escalation enabled (`EscalationModel = "small"`), confirming:
- Real-speech recordings are unchanged (no spurious escalation, identical transcript).
- The synthetic hallucination clip's flagged segment is escalated and then dropped (Case A) rather than typed out.

Document the observed before/after in the PR description.

- [ ] **Step 3: Commit any harness/doc artifacts (if kept)**

```bash
git add -A
git commit -m "docs: record escalation end-to-end verification"
```

---

## Notes for the implementer

- **Calibration:** `LowConfidenceThreshold = 0.05` is from a single sample. If real recordings show false positives (real speech escalated) or misses (hallucinations not escalated), adjust this constant and note the evidence.
- **Backward compatibility:** `EscalationModel = ""` must always reproduce today's behaviour. Do not change `FilterAnnotations` or `FormatSegments`.
- **Never crash the tray:** all escalation failures are swallowed (logged) and fall back to keeping cleaned text.
