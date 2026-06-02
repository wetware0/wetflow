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
