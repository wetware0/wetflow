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
    public void Span_NearEnd_ClampsToLength()
    {
        // 1.9s..2.0s in a 2s (64000-byte) buffer: expansion would push end past
        // the buffer, so it clamps to pcmLength while the start stays put.
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(2.0), pcmLength: 64000);
        Assert.Equal(46400, start);
        Assert.Equal(17600, length);
    }

    [Fact]
    public void Span_NearStart_ClampsToZero()
    {
        var (start, length) = SegmentResolver.ComputeSpan(
            TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(0.2), pcmLength: 32000);
        Assert.Equal(0, start);
        Assert.Equal(19200, length); // expansion clamped at start only (end stays within bounds)
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

public class SegmentResolverResolveTests
{
    private static SegmentResolver.RawSegment Raw(string text, double startSecs, double endSecs, float minProb)
        => new(text, TimeSpan.FromSeconds(startSecs), TimeSpan.FromSeconds(endSecs), minProb);

    // Uses the real Transcriber.FilterAnnotations so tests cover the actual annotation stripping.
    private static string Strip(string s) => Transcriber.FilterAnnotations(s);

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

    [Fact]
    public async Task MultiSegment_MixedClasses_ResolvedInOrder()
    {
        var segs = new[]
        {
            Raw("First clean", 0, 1, 0.9f),
            Raw("[BLANK_AUDIO]", 1, 2, 0.9f),
            Raw("garbled hallucination", 2, 3, 0.01f),
            Raw("Last clean", 3, 4, 0.9f),
        };
        var result = await SegmentResolver.ResolveAsync(segs, 1_000_000, Strip, Escalator("recovered words", 0.8f), default);
        Assert.Equal(3, result.Count);
        Assert.Equal("First clean", result[0].Text);   // Clean kept
        Assert.Equal("recovered words", result[1].Text); // Blank dropped; Flagged replaced
        Assert.Equal("Last clean", result[2].Text);
    }
}
