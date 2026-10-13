using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class TranscriptSignalTests
{
    private const string Root = @"C:\transcripts";
    private const string A = @"C:\transcripts\proj\a.jsonl";
    private const string B = @"C:\transcripts\proj\b.jsonl";
    private const string Observer = @"C:\transcripts\Claude-Mem-Observer\o.jsonl";
    private static readonly TimeSpan T = TimeSpan.FromHours(1);

    private sealed class FakeScanner : IFileScanner
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string>? LastRoots { get; private set; }

        public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
        {
            LastRoots = roots;
            return new Dictionary<string, long>(Files, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static TranscriptSignal Create(FakeScanner scanner, params string[] ignore) =>
        new(scanner, [Root], ignore);

    [Fact]
    public void Name_is_transcripts()
    {
        Assert.Equal("transcripts", Create(new FakeScanner()).Name);
    }

    [Fact]
    public void First_sample_records_baseline_and_is_not_busy()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner);

        Assert.False(signal.Sample(T));
        Assert.Null(signal.LastChangedPath);
        Assert.Equal([Root], scanner.LastRoots);
    }

    [Fact]
    public void Growing_file_is_busy_and_unchanged_files_are_not()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        scanner.Files[B] = 50;
        var signal = Create(scanner);
        signal.Sample(T);

        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));

        scanner.Files[B] = 60;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(20)));
        Assert.Equal(B, signal.LastChangedPath);

        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void New_file_is_busy()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner);
        signal.Sample(T);

        scanner.Files[B] = 1;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(10)));
        Assert.Equal(B, signal.LastChangedPath);
    }

    [Fact]
    public void Shrink_or_delete_is_not_busy_but_later_growth_is()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        scanner.Files[B] = 50;
        var signal = Create(scanner);
        signal.Sample(T);

        scanner.Files[A] = 10;
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));  // shrink

        scanner.Files.Remove(B);
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(20)));  // delete
        Assert.Null(signal.LastChangedPath);

        scanner.Files[A] = 20;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(30)));   // the surviving file grows
        Assert.Equal(A, signal.LastChangedPath);

        scanner.Files[B] = 1;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(40)));   // the deleted file is created again
        Assert.Equal(B, signal.LastChangedPath);
    }

    [Fact]
    public void Ignored_paths_never_count_and_matching_ignores_case()
    {
        var scanner = new FakeScanner();
        scanner.Files[Observer] = 100;
        var signal = Create(scanner, "claude-mem-observer");
        signal.Sample(T);

        scanner.Files[Observer] = 200;
        scanner.Files[@"C:\transcripts\CLAUDE-MEM-OBSERVER\new.jsonl"] = 1;
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));
        Assert.Null(signal.LastChangedPath);
    }

    [Fact]
    public void Blank_ignore_entry_ignores_nothing()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner, "");
        signal.Sample(T);

        scanner.Files[A] = 101;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(10)));
    }
}
