using Whisper.net;
using Whisper.net.Ggml;

namespace WetFlow;

public sealed class Transcriber : IDisposable
{
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public event Action<string>? StatusChanged;

    public async Task<string> TranscribeAsync(string wavPath, string modelName = "base")
    {
        await EnsureInitializedAsync(modelName);

        if (_factory == null)
            return string.Empty;

        using var processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        using var fileStream = File.OpenRead(wavPath);
        var segments = new List<string>();

        await foreach (var segment in processor.ProcessAsync(fileStream))
            segments.Add(segment.Text);

        return string.Join(" ", segments).Trim();
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
