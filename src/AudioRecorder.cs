using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WetFlow;

public sealed class AudioRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _rawPath;
    private readonly object _lock = new();

    public void Start()
    {
        lock (_lock)
        {
            Stop();
            _rawPath = Path.Combine(Path.GetTempPath(), $"wetflow_raw_{Guid.NewGuid():N}.wav");
            _capture = new WasapiCapture();
            _writer = new WaveFileWriter(_rawPath, _capture.WaveFormat);

            _capture.DataAvailable += (_, e) =>
            {
                lock (_lock)
                    _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _capture.StartRecording();
        }
    }

    public string? Stop()
    {
        lock (_lock)
        {
            if (_capture == null) return null;

            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            var rawPath = _rawPath;
            _rawPath = null;

            if (rawPath == null || !File.Exists(rawPath)) return null;

            try
            {
                using (var reader = new WaveFileReader(rawPath))
                {
                    // Skip ultra-short recordings (e.g. key tapped) — Whisper rejects empty/silent input.
                    if (reader.TotalTime < TimeSpan.FromMilliseconds(200))
                        return null;

                    var outPath = Path.Combine(Path.GetTempPath(), $"wetflow_{Guid.NewGuid():N}.wav");
                    var targetFormat = new WaveFormat(16000, 16, 1);
                    using var resampler = new MediaFoundationResampler(reader, targetFormat);
                    resampler.ResamplerQuality = 60;
                    WaveFileWriter.CreateWaveFile(outPath, resampler);
                    return outPath;
                }
            }
            finally
            {
                try { File.Delete(rawPath); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _writer?.Dispose();
        if (_rawPath != null)
        {
            try { File.Delete(_rawPath); } catch { }
        }
    }
}
