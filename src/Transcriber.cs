using System.Diagnostics;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace WetFlow;

public sealed class Transcriber : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _currentModelName;
    private bool _currentUseGpu;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public event Action<string>? StatusChanged;

    public async Task<string> TranscribeAsync(string wavPath, string modelName = "base",
        double shortPauseSecs = 0.5, double longPauseSecs = 1.5,
        bool useGpu = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(modelName, useGpu);
        cancellationToken.ThrowIfCancellationRequested();

        if (_processor == null)
            return string.Empty;

        var chunks = BuildChunks(wavPath, shortPauseSecs, longPauseSecs);

        if (chunks.Count == 1 && chunks[0].ChunkPath == wavPath)
        {
            var singleSw = Stopwatch.StartNew();
            using var fileStream = File.OpenRead(wavPath);
            var segments = new List<(string Text, TimeSpan Start, TimeSpan End)>();
            await foreach (var segment in _processor!.ProcessAsync(fileStream).WithCancellation(cancellationToken))
                segments.Add((segment.Text, segment.Start, segment.End));
            singleSw.Stop();
            TrayApp.Log($"[TIMING] chunk-transcription: {singleSw.ElapsedMilliseconds} ms");
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

                var chunkSw = Stopwatch.StartNew();
                using var fs = File.OpenRead(chunkPath);
                var segs = new List<(string Text, TimeSpan Start, TimeSpan End)>();
                await foreach (var seg in _processor!.ProcessAsync(fs).WithCancellation(cancellationToken))
                    segs.Add((seg.Text, seg.Start, seg.End));
                chunkSw.Stop();
                TrayApp.Log($"[TIMING] chunk-transcription: {chunkSw.ElapsedMilliseconds} ms");
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

    private async Task EnsureInitializedAsync(string modelName, bool useGpu)
    {
        if (_currentModelName == modelName && _currentUseGpu == useGpu) return;

        await _initLock.WaitAsync();
        try
        {
            if (_currentModelName == modelName && _currentUseGpu == useGpu) return;

            // Dispose old processor + factory when config changes
            if (_processor != null) { await _processor.DisposeAsync(); _processor = null; }
            _factory?.Dispose();
            _factory = null;

            var (modelType, quantType) = ParseModel(modelName);

            var modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "wetflow", "models");
            Directory.CreateDirectory(modelDir);

            var modelPath = Path.Combine(modelDir, $"ggml-{modelName}.bin");

            if (!File.Exists(modelPath))
            {
                StatusChanged?.Invoke($"Downloading Whisper {modelName} model (first run)…");
                using var httpClient = new System.Net.Http.HttpClient();
                var downloader = new WhisperGgmlDownloader(httpClient);
                var modelStream = await downloader.GetGgmlModelAsync(modelType, quantType);
                using var fs = File.Create(modelPath);
                await modelStream.CopyToAsync(fs);
            }

            if (useGpu)
            {
                try
                {
                    _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
                }
                catch (Exception ex)
                {
                    TrayApp.Log($"GPU init failed, falling back to CPU: {ex.Message}");
                    _factory = WhisperFactory.FromPath(modelPath);
                }
            }
            else
            {
                _factory = WhisperFactory.FromPath(modelPath);
            }

            _processor = _factory.CreateBuilder().WithLanguage("auto").Build();
            _currentModelName = modelName;
            _currentUseGpu = useGpu;
            StatusChanged?.Invoke("Ready");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static (GgmlType Type, QuantizationType Quant) ParseModel(string modelName) => modelName switch
    {
        "tiny" => (GgmlType.Tiny, QuantizationType.NoQuantization),
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
        _processor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _factory?.Dispose();
        _initLock.Dispose();
    }
}
