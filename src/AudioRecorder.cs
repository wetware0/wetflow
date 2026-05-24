using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WetFlow;

public sealed class AudioRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private readonly object _lock = new();

    public void Start()
    {
        lock (_lock)
        {
            Stop();
            _capture = new WasapiCapture();
            _buffer = new MemoryStream();
            _writer = new WaveFileWriter(_buffer, _capture.WaveFormat);

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

            if (_buffer == null || _buffer.Length == 0)
            {
                _buffer?.Dispose();
                _buffer = null;
                return null;
            }

            // Resample to 16kHz mono 16-bit for Whisper
            _buffer.Position = 0;
            var tempPath = Path.Combine(Path.GetTempPath(), $"wetflow_{Guid.NewGuid():N}.wav");

            using var reader = new WaveFileReader(_buffer);
            var targetFormat = new WaveFormat(16000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, targetFormat);
            resampler.ResamplerQuality = 60;
            WaveFileWriter.CreateWaveFile(tempPath, resampler);

            _buffer.Dispose();
            _buffer = null;
            return tempPath;
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _writer?.Dispose();
        _buffer?.Dispose();
    }
}
