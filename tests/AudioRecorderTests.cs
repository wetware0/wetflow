using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class AudioRecorderTests
{
    [Fact]
    public void ComputeRms_TooShort_ReturnsZero()
    {
        Assert.Equal(0f, AudioRecorder.ComputeRms(Array.Empty<byte>(), 0));
        Assert.Equal(0f, AudioRecorder.ComputeRms(new byte[1], 1));
    }

    [Fact]
    public void ComputeRms_Silence_ReturnsZero()
    {
        var buf = new byte[64];
        Assert.Equal(0f, AudioRecorder.ComputeRms(buf, buf.Length));
    }

    [Fact]
    public void ComputeRms_FullScale_ClampedToOne()
    {
        var buf = new byte[64];
        for (int i = 0; i < buf.Length; i += 2) { buf[i] = 0xFF; buf[i + 1] = 0x7F; }
        Assert.Equal(1f, AudioRecorder.ComputeRms(buf, buf.Length));
    }

    [Fact]
    public void ComputeRms_NormalSpeech_InRange()
    {
        // 0x1000 little-endian = 4096, normalised ~0.125 amplitude
        var buf = new byte[64];
        for (int i = 0; i < buf.Length; i += 2) { buf[i] = 0x00; buf[i + 1] = 0x10; }
        float rms = AudioRecorder.ComputeRms(buf, buf.Length);
        Assert.InRange(rms, 0f, 1f);
        Assert.True(rms > 0f);
    }
}
