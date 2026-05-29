using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TrayAppTests
{
    [Theory]
    [InlineData("hello", false, null, false)]             // success → delete
    [InlineData("", false, null, true)]                   // empty transcript → preserve
    [InlineData("  ", false, null, true)]                 // whitespace-only → preserve
    [InlineData(null, true, null, false)]                 // cancelled → delete
    [InlineData("hello", false, typeof(IOException), true)] // exception → preserve
    public void ShouldPreserveAudio_ReturnsExpected(
        string? text, bool wasCancelled, Type? exType, bool expected)
    {
        var ex = exType != null ? (Exception)Activator.CreateInstance(exType)! : null;
        Assert.Equal(expected, TrayApp.ShouldPreserveAudio(text, wasCancelled, ex));
    }
}

public class PruneOldAudioFilesTests
{
    [Fact]
    public void KeepsNewest3_WhenMoreThan3FilesExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var names = new[]
            {
                "wetflow_20240101_000000_000.wav",
                "wetflow_20240102_000000_000.wav",
                "wetflow_20240103_000000_000.wav",
                "wetflow_20240104_000000_000.wav",
                "wetflow_20240105_000000_000.wav",
            };
            foreach (var n in names)
                File.WriteAllBytes(Path.Combine(dir, n), []);

            TrayApp.PruneOldAudioFiles(dir);

            var remaining = Directory.GetFiles(dir, "*.wav")
                .Select(Path.GetFileName)
                .OrderBy(f => f)
                .ToArray();
            var expected = new[]
            {
                "wetflow_20240103_000000_000.wav",
                "wetflow_20240104_000000_000.wav",
                "wetflow_20240105_000000_000.wav",
            };
            Assert.Equal(expected, remaining);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public void LeavesFilesUntouched_WhenAtMost3Exist(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < count; i++)
                File.WriteAllBytes(Path.Combine(dir, $"wetflow_2024010{i + 1}_000000_000.wav"), []);

            TrayApp.PruneOldAudioFiles(dir);

            Assert.Equal(count, Directory.GetFiles(dir, "*.wav").Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
