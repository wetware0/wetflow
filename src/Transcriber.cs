using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.Ggml;

namespace WetFlow;

public sealed class Transcriber : IDisposable
{
    private WhisperFactory? _factory;
    private string? _currentModelName;
    private bool _currentUseGpu;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private WhisperFactory? _escalationFactory;
    private string? _currentEscalationModel;
    private bool _currentEscalationUseGpu;
    private bool _escalationLoadFailed;
    private readonly SemaphoreSlim _escalationInitLock = new(1, 1);

    public event Action<string>? StatusChanged;

    public async Task<string> TranscribeAsync(string wavPath, string modelName = "base",
        double shortPauseSecs = 0.5, double longPauseSecs = 1.5,
        bool useGpu = false, string escalationModel = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(modelName, useGpu);
        cancellationToken.ThrowIfCancellationRequested();

        var factory = _factory;
        if (factory == null)
        {
            TrayApp.Log("[WARN] _factory is null at transcription start — returning empty.");
            return string.Empty;
        }

        // Create a fresh processor per transcription so the model's inference
        // context window does not carry over between recordings.
        // (No NoContext/NoSpeechThreshold options — hallucinations are handled downstream by SegmentResolver.)
        await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();

        var chunks = BuildChunks(wavPath, shortPauseSecs, longPauseSecs);

        if (chunks.Count == 1 && chunks[0].ChunkPath == wavPath)
        {
            var segments = await TranscribeChunkAsync(wavPath, processor, useGpu, escalationModel, cancellationToken);
            return FormatSegments(segments, shortPauseSecs, longPauseSecs);
        }

        var sb = new StringBuilder();
        string carrySep = "";
        try
        {
            foreach (var (chunkPath, sepBefore) in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sepBefore == "\n\n" || (carrySep != "\n\n" && sepBefore.Length > 0))
                    carrySep = sepBefore;

                var segs = await TranscribeChunkAsync(chunkPath, processor, useGpu, escalationModel, cancellationToken);
                var text = FormatSegments(segs, shortPauseSecs, longPauseSecs).Trim();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0) sb.Append(carrySep);
                    sb.Append(text);
                    carrySep = "";
                }
            }
        }
        finally
        {
            foreach (var (chunkPath, _) in chunks)
                try { File.Delete(chunkPath); } catch { }
        }

        return sb.ToString().Trim();
    }

    internal static string FormatSegments(
        IReadOnlyList<(string Text, TimeSpan Start, TimeSpan End)> segments,
        double shortPauseSecs, double longPauseSecs)
    {
        if (segments.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append(segments[0].Text.Trim());

        for (int i = 1; i < segments.Count; i++)
        {
            var gap = (segments[i].Start - segments[i - 1].End).TotalSeconds;

            if (gap >= longPauseSecs)
                sb.Append("\n\n");
            else if (gap >= shortPauseSecs)
                sb.Append("\n");
            else
                sb.Append(" ");

            sb.Append(segments[i].Text.Trim());
        }

        return sb.ToString().Trim();
    }

    // Splits the WAV file at detected silence midpoints; returns [(chunkPath, separatorBefore)].
    // When no significant silences exist, returns [(wavPath, "")] so the caller skips chunk splitting.
    private static List<(string ChunkPath, string SepBefore)> BuildChunks(
        string wavPath, double shortPauseSecs, double longPauseSecs)
    {
        var silences = DetectSilencePeriods(wavPath)
            .Where(s => (s.End - s.Start).TotalSeconds >= longPauseSecs)
            .ToList();

        if (silences.Count == 0)
            return [(wavPath, "")];

        byte[] pcm;
        using (var fs = File.OpenRead(wavPath))
        {
            fs.Seek(44, SeekOrigin.Begin);
            pcm = new byte[fs.Length - 44];
            _ = fs.Read(pcm, 0, pcm.Length);
        }

        const int sampleRate = 16000;
        const int bytesPerSample = 2;
        var chunks = new List<(string, string)>();
        int prevByte = 0;

        for (int i = 0; i <= silences.Count; i++)
        {
            string sepBefore = i == 0 ? "" : "\n\n";

            int nextByte = i < silences.Count
                ? (int)(((silences[i].Start.TotalSeconds + silences[i].End.TotalSeconds) / 2) * sampleRate * bytesPerSample)
                : pcm.Length;
            nextByte = Math.Clamp(nextByte, prevByte, pcm.Length);

            if (nextByte > prevByte)
            {
                var chunkPath = Path.Combine(Path.GetTempPath(), $"wetflow_c_{Guid.NewGuid():N}.wav");
                WritePcmWav(chunkPath, pcm, prevByte, nextByte - prevByte, sampleRate);
                chunks.Add((chunkPath, sepBefore));
            }

            prevByte = nextByte;
        }

        return chunks.Count > 0 ? chunks : [(wavPath, "")];
    }

    // Scans a 16 kHz / 16-bit / mono WAV (44-byte header) for stretches of audio
    // below the RMS threshold of at least minSecs duration.
    private static List<(TimeSpan Start, TimeSpan End)> DetectSilencePeriods(string wavPath)
    {
        const int sampleRate = 16000;
        const int windowSamples = 400; // 25 ms
        const double rmsThreshold = 600.0;
        const double minSecs = 0.3;

        using var fs = File.OpenRead(wavPath);
        fs.Seek(44, SeekOrigin.Begin);

        var window = new byte[windowSamples * 2];
        var result = new List<(TimeSpan, TimeSpan)>();
        long sample = 0;
        bool inSilence = false;
        long silStart = 0;

        int n;
        while ((n = fs.Read(window, 0, window.Length)) > 0)
        {
            int count = n / 2;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                short s = BitConverter.ToInt16(window, i * 2);
                sum += (double)s * s;
            }
            double rms = count > 0 ? Math.Sqrt(sum / count) : 0;

            if (rms < rmsThreshold)
            {
                if (!inSilence) { inSilence = true; silStart = sample; }
            }
            else if (inSilence)
            {
                inSilence = false;
                double dur = (double)(sample - silStart) / sampleRate;
                if (dur >= minSecs)
                    result.Add((TimeSpan.FromSeconds((double)silStart / sampleRate),
                                TimeSpan.FromSeconds((double)sample / sampleRate)));
            }
            sample += count;
        }

        return result;
    }

    private static void WritePcmWav(string path, byte[] pcm, int offset, int length, int sampleRate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(length);
        w.Write(pcm, offset, length);
    }

    private static readonly Regex _annotationPattern =
        new(@"\[[^\]]+\]|\([^)]+\)", RegexOptions.Compiled);

    private static readonly Regex _whitespacePattern =
        new(@"\s+", RegexOptions.Compiled);

    internal static string FilterAnnotations(string text)
    {
        return _whitespacePattern.Replace(_annotationPattern.Replace(text, " "), " ").Trim();
    }

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
        // Only needed for escalation slicing; skip the read entirely when disabled.
        var pcm = string.IsNullOrWhiteSpace(escalationModel) ? Array.Empty<byte>() : ReadPcm(chunkPath);

        List<SegmentResolver.RawSegment> raw;
        using (var fs = File.OpenRead(chunkPath))
            raw = await TranscribeStreamAsync(processor, fs, ct);

        async Task<SegmentResolver.EscalationResult> Escalate((int Start, int Length) span, CancellationToken token)
        {
            var escFactory = await EnsureEscalationFactoryAsync(escalationModel, useGpu);
            if (escFactory == null) throw new InvalidOperationException("escalation unavailable");
            if (span.Length == 0) return new SegmentResolver.EscalationResult("", 1f);

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

    internal static (GgmlType Type, QuantizationType Quant) ParseModel(string modelName) => modelName switch
    {
        "tiny" => (GgmlType.Tiny, QuantizationType.NoQuantization),
        "base" => (GgmlType.Base, QuantizationType.NoQuantization),
        "base.en" => (GgmlType.BaseEn, QuantizationType.NoQuantization),
        "base-q5_1" => (GgmlType.Base, QuantizationType.Q5_1),
        "small" => (GgmlType.Small, QuantizationType.NoQuantization),
        "small.en" => (GgmlType.SmallEn, QuantizationType.NoQuantization),
        "small-q5_1" => (GgmlType.Small, QuantizationType.Q5_1),
        "medium" => (GgmlType.Medium, QuantizationType.NoQuantization),
        _ => (GgmlType.Base, QuantizationType.NoQuantization)
    };

    public void Dispose()
    {
        _factory?.Dispose();
        _escalationFactory?.Dispose();
        _initLock.Dispose();
        _escalationInitLock.Dispose();
    }
}
