# Transcription Hallucination Escalation — Design

**Date:** 2026-06-02
**Status:** Approved (pending spec review)
**Builds on:** PR #37 (`FilterAnnotations` annotation-token filter)
**Related issue:** #36 — "Transcription sometimes shows [Music] (gunshots), etc. and loses the actual transcript"

## Problem

Whisper is autoregressive and always emits text. On audio with no clearly
intelligible speech it falls back to its training prior and invents plausible
output — bracket annotations (`[Music]`, `(gunfire)`) or, worse, plausible
*sentences* (`"I am very happy to see you again"`, `"Thank you"`). The latter
have no markers, so the PR #37 regex filter cannot catch them and they end up
typed at the cursor as a wrong transcript.

Empirically verified (Whisper.net 1.8.1, `base-q5_1`):

- Segment-level `Probability` / `MinProbability` / `MaxProbability` are **always
  0.0** — unusable.
- Segment-level `NoSpeechProbability` is **0.0 for both real speech and the
  markerless hallucination** — cannot discriminate. (It is high, ~0.94, only for
  true `[BLANK_AUDIO]` silence.)
- **Per-token** probability *can* discriminate: the markerless hallucination had
  a minimum token probability of **0.010**, whereas the lowest real-speech
  segment floored at **0.119**.
- `WithNoContext()` and `WithNoSpeechThreshold()` produced **byte-identical
  output** across every clip and parameter value tested — they are inert here.

## Two failure modes (and what "fix" means for each)

- **Case A — nothing recoverable.** True silence/noise/glitch. The correct
  transcript is *empty*. No model can recover words that are not in the audio;
  the correct action is to **emit nothing**.
- **Case B — real speech mis-transcribed.** Quiet/accented/fast speech the base
  model mangled into a hallucination. A larger model genuinely **can** recover
  the words. This is the only case where re-transcription produces new correct
  text.

## Strategy: detect → escalate to a larger local model → splice or drop

Offline-only (no cloud). Per-segment granularity (Approach A) so latency is
bounded to the flagged span regardless of recording length.

### 1. Detection & classification

`TranscribeStreamAsync` is extended to capture each segment's **minimum token
probability** (over non-special tokens). Let `cleaned = FilterAnnotations(rawText)`.
Each raw segment is classified:

| Bucket | Rule | Action |
|--------|------|--------|
| **Blank** | `cleaned` is empty **and** the segment was `[BLANK_AUDIO]` | drop, **no escalation** |
| **Flagged** | (`cleaned` is empty **and** it was a *non-blank* annotation, e.g. `[Music]`) **OR** `minTokenProb < LowConfidenceThreshold` | escalate |
| **Clean** | otherwise (`cleaned` non-empty **and** confident) | keep `cleaned` (current behaviour) |

This means a high-confidence *mixed* segment like `"Hello [Music] world"` stays
**Clean** — stripped to `"Hello world"` exactly as PR #37 does today, with no
needless escalation. Escalation fires only on pure-annotation segments
(`[Music]` with nothing else) or genuinely low-confidence ones (the markerless
hallucination case).

`LowConfidenceThreshold = 0.05f` — a named constant, sitting between the observed
hallucination (0.010) and the real-speech floor (0.119).

> **Calibration caveat:** based on a single hallucination sample. Must be
> re-validated against real flagged recordings. Kept as a constant (not a
> setting) for v1 — YAGNI.

### 2. Escalation & merge (per flagged segment)

1. Slice the segment's audio span `[Start, End]` from the chunk PCM (reusing the
   existing `WritePcmWav`). If shorter than 1.0s, expand symmetrically to 1.0s
   (clamped to chunk bounds) so the model has a usable window. No padding beyond
   that — avoids duplicating neighbouring words on splice.
2. Run the escalation model over that short clip; collect its segments, apply
   `FilterAnnotations`, compute its minimum token probability.
3. Decide:
   - Result non-empty **and** confident (`minTokenProb >= LowConfidenceThreshold`)
     → **replace** the segment's text, keep its original `Start`/`End`.
   - Otherwise (empty / blank / still low-confidence) → **drop** the segment
     (Case A — nothing recoverable).

Escalation never re-escalates its own output (single retry per segment).

### 3. Model management & settings

- New setting `AppSettings.EscalationModel` (string, default `"small"`).
  `ParseModel` already maps `small` / `small-q5_1` / `medium` / etc.
- A second, lazily-loaded, session-cached `_escalationFactory`, downloaded on
  first trigger via the existing `WhisperGgmlDownloader` path. The factory
  builder mirrors the primary (`WithLanguage("auto")`), with **no** NoContext /
  NoSpeechThreshold options.
- The factory-loading core (download + `FromPath` + GPU fallback) is refactored
  into a shared `CreateFactoryAsync(modelName, useGpu)` used by both the primary
  and escalation init paths.

### 4. Error handling & backward-compatibility

- **`EscalationModel` empty/null → feature OFF → exactly today's behaviour**
  (brackets stripped, low-confidence segments kept, nothing dropped). Safe,
  opt-out change.
- Escalation download/init/inference failure → log, **keep the cleaned original
  text** (never drop on error, never crash the tray, per existing app contract).
  A load failure is cached for the session so it is not retried per segment.
- Cancellation tokens propagate through escalation.
- Memory: the escalation factory (`small` ≈ 466 MB on disk; comparable RAM) is
  cached for the session and disposed in `Dispose()`.

### 5. Cleanup

Revert commit `70bc3b8` — **both** `WithNoContext()` and
`WithNoSpeechThreshold()`. Proven inert in testing; shipping config that implies
a benefit it does not deliver is worse than omitting it. This restores the
primary builder to `factory.CreateBuilder().WithLanguage("auto").Build()`.

## Components & boundaries

- **`SegmentResolver`** (new, internal static) — pure logic, no model/IO:
  - `Classify(rawText, minProb) -> {Clean, Blank, Flagged}`
  - `ResolveAsync(rawSegments, escalate)` where
    `escalate: Func<(int startByte, int len), Task<(string text, float minProb)>>`
    is injected — walks segments, applies the replace/drop/keep decision, returns
    the final `(Text, Start, End)` list. Fully unit-testable without a model.
  - Audio-span byte-offset math (TimeSpan → byte offset, min-1s expansion,
    clamping).
- **`Transcriber`** — owns factories, model download/IO, WAV slicing, and wires
  the real escalation processor into `SegmentResolver.ResolveAsync`. Continues to
  own `TranscribeStreamAsync` / `FormatSegments` / `BuildChunks`.

## Testing

Pure-logic unit tests (no native inference, per project rule):

- `Classify`: pure `[Music]`→Flagged, `[BLANK_AUDIO]`→Blank, high-prob speech→Clean,
  high-prob `"Hello [Music] world"`→Clean (kept as `"Hello world"`),
  low-prob text→Flagged.
- `ResolveAsync` with an injected fake escalator: flagged→replaced when escalator
  returns confident text; flagged→dropped when escalator returns empty/low-conf;
  clean→untouched; blank→dropped; escalator throws→original kept.
- Slice offset math: TimeSpan→byte offset, min-1s expansion, bounds clamping.

Actual Whisper escalation inference is **not** unit-tested (consistent with the
project's no-native-tests constraint); verified end-to-end with the standalone
A/B harness against synthetic and real failed-audio clips.

## Out of scope

- Cloud ASR fallback.
- Making `LowConfidenceThreshold` a user setting.
- Whole-file or whole-chunk re-transcription with text alignment (rejected:
  unbounded latency, fuzzy merge).
- Recovering genuinely unintelligible audio (Case A is correctly emitted as
  nothing).
