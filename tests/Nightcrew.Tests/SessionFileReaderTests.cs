using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SessionFileReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public SessionFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    [Fact]
    public void Reads_only_digit_named_json_files()
    {
        Write("123.json", "{\"status\":\"busy\"}");
        Write("123.key", "token");
        Write("abc.json", "{}");
        Write("123.json.tmp", "{}");
        Write("12a.json", "{}");

        var texts = new SessionFileReader().Read(_dir);

        Assert.Equal(["{\"status\":\"busy\"}"], texts);
    }

    [Fact]
    public void Skips_files_over_64_KB()
    {
        Write("1.json", new string(' ', 65_537));
        Write("2.json", "{}");

        Assert.Equal(["{}"], new SessionFileReader().Read(_dir));
    }

    [Fact]
    public void Reads_at_most_64_files()
    {
        for (var pid = 1; pid <= 70; pid++) Write(pid + ".json", "{}");

        Assert.Equal(64, new SessionFileReader().Read(_dir).Count);
    }

    [Fact]
    public void Missing_folder_gives_nothing()
    {
        Assert.Empty(new SessionFileReader().Read(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Expands_environment_variables_in_the_folder()
    {
        Write("7.json", "{}");
        Environment.SetEnvironmentVariable("NIGHTCREW_TEST_SESSIONS", _dir);

        Assert.Equal(["{}"], new SessionFileReader().Read("%NIGHTCREW_TEST_SESSIONS%"));
    }
}
