using System.Diagnostics;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class WatchingJsonlScannerTests : IDisposable
{
    private const string EnvVar = "NIGHTCREW_WATCHER_TEST_ROOT";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CountingScanner _lister = new();
    private readonly FakeClock _clock = new();
    private readonly WatchingJsonlScanner _scanner;

    public WatchingJsonlScannerTests() => _scanner = new WatchingJsonlScanner(_lister, _clock);

    public void Dispose()
    {
        _scanner.Dispose();
        Environment.SetEnvironmentVariable(EnvVar, null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The real lister, counting how often a root is listed in full.</summary>
    private sealed class CountingScanner : IFileScanner
    {
        private readonly JsonlFileScanner _real = new();

        public int Listings { get; private set; }

        public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
        {
            Listings += roots.Count;
            return _real.Scan(roots);
        }
    }

    private string Root => Path.Combine(_dir, "claude");

    private string Write(string relativePath, int bytes)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private IReadOnlyDictionary<string, long> ScanNextTick(params string[] roots)
    {
        _clock.Advance(Tick);
        return _scanner.Scan(roots.Length == 0 ? [Root] : roots);
    }

    /// <summary>
    /// Change notifications arrive on another thread, so keep scanning until they show up. The clock stays put,
    /// so the ten-minute safety listing can't be what finds the change.
    /// </summary>
    private void ScanUntil(Func<IReadOnlyDictionary<string, long>, bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done(_scanner.Scan([Root])))
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the change never showed up");
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void First_scan_lists_every_transcript_with_its_length()
    {
        var top = Write(@"claude\a.jsonl", 3);
        var deep = Write(@"claude\proj\sub\b.jsonl", 5);
        Write(@"claude\notes.txt", 7);

        var files = ScanNextTick();

        Assert.Equal(2, files.Count);
        Assert.Equal(3, files[top]);
        Assert.Equal(5, files[deep]);
        Assert.Equal(1, _lister.Listings);
    }

    [Fact]
    public void Later_scans_do_not_list_the_folder_again()
    {
        var a = Write(@"claude\a.jsonl", 3);

        ScanNextTick();
        ScanNextTick();
        var files = ScanNextTick();

        Assert.Equal(3, files[a]);
        Assert.Equal(1, _lister.Listings);
    }

    [Fact]
    public void A_transcript_that_grows_shows_its_new_length_without_a_new_listing()
    {
        var a = Write(@"claude\proj\a.jsonl", 3);
        ScanNextTick();

        File.AppendAllText(a, "{\"type\":\"assistant\"}\n");

        ScanUntil(files => files[a] == 3 + 21);
        Assert.Equal(1, _lister.Listings);
    }

    [Fact]
    public void A_new_transcript_appears_and_a_deleted_one_disappears_without_a_new_listing()
    {
        var old = Write(@"claude\proj\old.jsonl", 4);
        ScanNextTick();

        var created = Write(@"claude\other\new.jsonl", 6);
        File.Delete(old);

        ScanUntil(files => files.TryGetValue(created, out var length) && length == 6 && !files.ContainsKey(old));
        Assert.Equal(1, _lister.Listings);
    }

    [Fact]
    public void Renaming_a_transcript_to_another_extension_removes_it()
    {
        var a = Write(@"claude\proj\a.jsonl", 4);
        ScanNextTick();

        var renamed = Path.ChangeExtension(a, ".bak");
        File.Move(a, renamed);

        ScanUntil(files => files.Count == 0);
    }

    [Fact]
    public void Lists_again_every_ten_minutes_as_a_safety_net()
    {
        Write(@"claude\a.jsonl", 3);
        ScanNextTick();

        _clock.Advance(TimeSpan.FromMinutes(10));
        _scanner.Scan([Root]);

        Assert.Equal(2, _lister.Listings);
    }

    [Fact]
    public void Lists_again_on_the_next_scan_after_the_watcher_missed_changes()
    {
        Write(@"claude\a.jsonl", 3);
        ScanNextTick();

        _scanner.ChangesMissed(Root);
        ScanNextTick();
        ScanNextTick();

        Assert.Equal(2, _lister.Listings);
    }

    [Fact]
    public void A_missing_root_is_listed_every_scan_until_it_exists_and_can_be_watched()
    {
        Assert.Empty(ScanNextTick());
        Assert.Empty(ScanNextTick());
        Assert.Equal(2, _lister.Listings);

        var a = Write(@"claude\a.jsonl", 3);
        Assert.Equal(3, ScanNextTick()[a]);
        Assert.Equal(3, _lister.Listings);

        ScanNextTick();
        Assert.Equal(3, _lister.Listings);
    }

    [Fact]
    public void A_root_that_is_deleted_and_created_again_is_watched_again()
    {
        Write(@"claude\proj\a.jsonl", 3);
        ScanNextTick();

        Directory.Delete(Root, recursive: true);
        ScanUntil(files => files.Count == 0);

        var b = Write(@"claude\proj\b.jsonl", 5);
        ScanUntil(files => files.TryGetValue(b, out var length) && length == 5);

        File.AppendAllText(b, "{}\n");
        ScanUntil(files => files[b] == 5 + 3);
    }

    [Fact]
    public void Roots_with_environment_variables_are_watched_too()
    {
        var a = Write(@"claude\a.jsonl", 3);
        Environment.SetEnvironmentVariable(EnvVar, _dir);
        var root = @"%" + EnvVar + @"%\claude";

        _scanner.Scan([root]);
        _clock.Advance(Tick);
        var files = _scanner.Scan([root]);

        Assert.Equal(3, files[a]);
        Assert.Equal(1, _lister.Listings);
    }
}
