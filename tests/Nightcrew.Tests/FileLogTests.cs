using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_a_line_that_can_be_read_back()
    {
        var path = Path.Combine(_dir, "nightcrew.log");
        var log = new FileLog(path);

        log.Info("hello");
        log.Warn("careful");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[INFO] hello", lines[0]);
        Assert.EndsWith("[WARN] careful", lines[1]);
    }

    [Fact]
    public void Logging_where_the_folder_is_a_file_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        var notAFolder = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(notAFolder, "");
        var log = new FileLog(Path.Combine(notAFolder, "nightcrew.log"));

        log.Info("dropped");
        log.Warn("dropped");

        Assert.Equal("", File.ReadAllText(notAFolder));
    }
}
