using Whisper.net;
using Whisper.net.Ggml;

namespace WetFlow;

public sealed class Transcriber : IDisposable
{
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public event Action<string>? StatusChanged;

    public async Task<string> TranscribeAsync(string wavPath, string modelName = "base",
        double shortPauseSecs = 0.5, double longPauseSecs = 1.5)
    {
        await EnsureInitializedAsync(modelName);

        if (_factory == null)
            return string.Empty;

        using var processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        using var fileStream = File.OpenRead(wavPath);
        var segments = new List<(string Text, TimeSpan Start, TimeSpan End)>();

        await foreach (var segment in processor.ProcessAsync(fileStream))
            segments.Add((segment.Text, segment.Start, segment.End));

        return FormatSegments(segments, shortPauseSecs, longPauseSecs);
    }

    internal static string FormatSegments(
        IReadOnlyList<(string Text, TimeSpan Start, TimeSpan End)> segments,
        double shortPauseSecs, double longPauseSecs)
    {
        if (segments.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
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

    private async Task EnsureInitializedAsync(string modelName)
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var modelType = modelName switch
            {
                "tiny" => GgmlType.Tiny,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                _ => GgmlType.Base
            };

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
                var modelStream = await downloader.GetGgmlModelAsync(modelType);
                using var fs = File.Create(modelPath);
                await modelStream.CopyToAsync(fs);
            }

            _factory = WhisperFactory.FromPath(modelPath);
            _initialized = true;
            StatusChanged?.Invoke("Ready");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _initLock.Dispose();
    }
}
