using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TranscriberTests
{
    private static (string Text, TimeSpan Start, TimeSpan End) Seg(string text, double startSecs, double endSecs)
        => (text, TimeSpan.FromSeconds(startSecs), TimeSpan.FromSeconds(endSecs));

    [Fact]
    public void FormatSegments_Empty_ReturnsEmpty()
    {
        var result = Transcriber.FormatSegments(Array.Empty<(string, TimeSpan, TimeSpan)>(), 0.5, 1.5);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatSegments_Single_ReturnsTrimmedText()
    {
        var result = Transcriber.FormatSegments(new[] { Seg(" hello ", 0, 1) }, 0.5, 1.5);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void FormatSegments_SmallGap_JoinsWithSpace()
    {
        var segs = new[] { Seg("Hello", 0, 1.0), Seg("world", 1.2, 2.0) };
        Assert.Equal("Hello world", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_ShortPauseGap_InsertsNewline()
    {
        var segs = new[] { Seg("Hello", 0, 1.0), Seg("world", 1.8, 2.5) };
        Assert.Equal("Hello\nworld", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_LongPauseGap_InsertsBlankLine()
    {
        var segs = new[] { Seg("Hello", 0, 1.0), Seg("world", 3.0, 4.0) };
        Assert.Equal("Hello\n\nworld", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_MixedGaps_FormatsCorrectly()
    {
        var segs = new[]
        {
            Seg("First", 0, 1.0),
            Seg("second", 1.2, 2.0),
            Seg("Third", 2.8, 3.5),
            Seg("Fourth", 5.5, 6.0),
        };
        Assert.Equal("First second\nThird\n\nFourth", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_ExactShortPauseGap_InsertsNewline()
    {
        // gap == shortPauseSecs exactly → >= is inclusive, so \n
        var segs = new[] { Seg("Hello", 0, 1.0), Seg("world", 1.5, 2.5) };
        Assert.Equal("Hello\nworld", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_ExactLongPauseGap_InsertsBlankLine()
    {
        // gap == longPauseSecs exactly → >= is inclusive, so \n\n
        var segs = new[] { Seg("Hello", 0, 1.0), Seg("world", 2.5, 3.5) };
        Assert.Equal("Hello\n\nworld", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }

    [Fact]
    public void FormatSegments_NegativeGap_JoinsWithSpace()
    {
        // Whisper can emit overlapping segments (end > next start)
        var segs = new[] { Seg("Hello", 0, 2.0), Seg("world", 1.5, 3.0) };
        Assert.Equal("Hello world", Transcriber.FormatSegments(segs, 0.5, 1.5));
    }
}

public class AnnotationFilterTests
{
    [Fact]
    public void Filter_BracketAnnotation_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations(" [Music]"));

    [Fact]
    public void Filter_ParenAnnotation_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations(" (gunfire)"));

    [Fact]
    public void Filter_BlankAudio_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations("[BLANK_AUDIO]"));

    [Fact]
    public void Filter_SpeechOnly_ReturnsUnchanged()
        => Assert.Equal("Hello world", Transcriber.FilterAnnotations("Hello world"));

    [Fact]
    public void Filter_MixedSpeechAndAnnotation_RetainsSpeech()
        => Assert.Equal("Hello world", Transcriber.FilterAnnotations("Hello [Music] world"));

    [Fact]
    public void Filter_AnnotationBeforeSpeech_RetainsSpeech()
        => Assert.Equal("Hello", Transcriber.FilterAnnotations("[Applause] Hello"));

    [Fact]
    public void Filter_MultipleAnnotations_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations("[Music] (gunfire) [Applause]"));

    [Fact]
    public void Filter_AnnotationAfterSpeech_RetainsSpeech()
        => Assert.Equal("Hello", Transcriber.FilterAnnotations("Hello [Music]"));

    [Fact]
    public void Filter_EmptyString_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations(""));

    [Fact]
    public void Filter_WhitespaceOnly_ReturnsEmpty()
        => Assert.Empty(Transcriber.FilterAnnotations("   "));
}
