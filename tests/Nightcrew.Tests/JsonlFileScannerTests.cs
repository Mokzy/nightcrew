using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class JsonlFileScannerTests : IDisposable
{
    private const string EnvVar = "NIGHTCREW_SCANNER_TEST_ROOT";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Touch(string relativePath, int bytes)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Finds_jsonl_files_recursively_with_their_lengths()
    {
        var top = Touch(@"claude\a.jsonl", 3);
        var deep = Touch(@"claude\proj\sub\b.jsonl", 5);
        Touch(@"claude\notes.txt", 7);
        Touch(@"claude\proj\c.json", 9);

        var files = new JsonlFileScanner().Scan([Path.Combine(_dir, "claude")]);

        Assert.Equal(2, files.Count);
        Assert.Equal(3, files[top]);
        Assert.Equal(5, files[deep]);
    }

    [Fact]
    public void Expands_environment_variables_and_skips_missing_roots()
    {
        var claude = Touch(@"claude\a.jsonl", 4);
        var codex = Touch(@"codex\2026\10\13\rollout.jsonl", 6);
        Environment.SetEnvironmentVariable(EnvVar, _dir);

        var files = new JsonlFileScanner().Scan(
            [@"%" + EnvVar + @"%\claude", Path.Combine(_dir, "missing"), Path.Combine(_dir, "codex")]);

        Assert.Equal(2, files.Count);
        Assert.Equal(4, files[claude]);
        Assert.Equal(6, files[codex]);
    }

    [Fact]
    public void No_roots_gives_an_empty_result()
    {
        Assert.Empty(new JsonlFileScanner().Scan([]));
    }
}
