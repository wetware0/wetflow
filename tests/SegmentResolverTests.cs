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
