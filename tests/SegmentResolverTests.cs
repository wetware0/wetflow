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
