using System.Text;
using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TranscriberCancellationTests
{
    [Fact]
    public async Task TranscribeAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var wavPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(wavPath, MinimalWavHeader());
            using var transcriber = new Transcriber();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transcriber.TranscribeAsync(wavPath, "base", cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    private static byte[] MinimalWavHeader()
    {
        // 44-byte RIFF/WAVE header with 0 data bytes — enough to open the stream
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
        h[16] = 16; // fmt chunk size
        h[20] = 1;  // PCM
        h[22] = 1;  // mono
        h[24] = 0x44; h[25] = 0xAC; // 44100 Hz
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        return h;
    }
}
