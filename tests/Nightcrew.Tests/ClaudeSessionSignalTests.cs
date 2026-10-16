using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ClaudeSessionSignalTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 10, 19, DateTimeKind.Utc);

    private readonly Files _files = new();
    private readonly Table _table = new();
    private readonly Settings _settings = new();
    private readonly ListLog _log = new();
    private LidState _lid = LidState.Open;

    private ClaudeSessionSignal NewSignal() => new(_files, _table, _settings, () => _lid, _log);

    private static string StatusFile(int pid, DateTime start, string status, string cwd = @"C:\work\app") =>
        $"{{\"pid\":{pid},\"sessionId\":\"abc\",\"cwd\":\"{cwd.Replace(@"\", @"\\")}\",\"procStart\":\"{start.ToFileTimeUtc()}\"," +
        $"\"status\":\"{status}\",\"name\":\"Personal website\",\"entrypoint\":\"cli\"}}";

    private void Session(int pid, string status, string cwd = @"C:\work\app")
    {
        _table.Start(pid, T0);
        _files.Texts.Add(StatusFile(pid, T0, status, cwd));
    }

    [Fact]
    public void Busy_session_counts()
    {
        Session(100, "busy");
        var signal = NewSignal();

        Assert.True(signal.Sample(TimeSpan.Zero));

        Assert.Equal([new Holder("Claude Code", "1 session")], signal.Holders);
        Assert.Equal("claude-status", signal.Name);
    }

    [Fact]
    public void Idle_session_does_not_count()
    {
        Session(100, "idle");

        Assert.False(NewSignal().Sample(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(true, LidState.Closed, true)]
    [InlineData(true, LidState.Open, false)]
    [InlineData(true, LidState.Unknown, false)]
    [InlineData(false, LidState.Closed, false)]
    public void Waiting_counts_only_with_the_setting_on_and_the_lid_closed(bool setting, LidState lid, bool expected)
    {
        _settings.StayAwakeForApprovals = setting;
        _lid = lid;
        Session(100, "waiting");

        Assert.Equal(expected, NewSignal().Sample(TimeSpan.Zero));
    }

    [Fact]
    public void Dead_process_does_not_count()
    {
        _files.Texts.Add(StatusFile(100, T0, "busy"));

        Assert.False(NewSignal().Sample(TimeSpan.Zero));
    }

    [Fact]
    public void Reused_pid_does_not_count()
    {
        _table.Start(100, T0.AddSeconds(1));
        _files.Texts.Add(StatusFile(100, T0, "busy"));

        Assert.False(NewSignal().Sample(TimeSpan.Zero));
    }

    [Fact]
    public void Empty_or_truncated_file_is_skipped()
    {
        _files.Texts.AddRange(["", "{\"pid\":10", "\0\0", "null", "{\"pid\":\"x\"}"]);
        var signal = NewSignal();

        Assert.False(signal.Sample(TimeSpan.Zero));
        Assert.False(signal.Sample(TimeSpan.FromSeconds(10)));

        Assert.Single(_log.Warnings); // once per run, however many files and samples
    }

    [Fact]
    public void Observer_sessions_are_ignored()
    {
        // claude-mem's background sessions run in this folder; the default IgnorePathContains matches the form
        // Claude Code gives the folder in its transcript paths.
        Session(100, "busy", @"C:\Users\someone\.claude-mem\observer-sessions");

        Assert.False(NewSignal().Sample(TimeSpan.Zero));
    }

    [Fact]
    public void Two_busy_sessions_show_as_two_sessions()
    {
        Session(100, "busy");
        Session(200, "busy");
        var signal = NewSignal();

        Assert.True(signal.Sample(TimeSpan.Zero));

        Assert.Equal([new Holder("Claude Code", "2 sessions")], signal.Holders);
    }

    [Fact]
    public void Nothing_from_the_file_reaches_the_log_or_holders()
    {
        Session(100, "busy", @"C:\secret-project");
        _files.Texts.Add("{\"cwd\":\"C:\\\\secret-project\",\"pid\":"); // unreadable, so it is warned about
        var signal = NewSignal();

        signal.Sample(TimeSpan.Zero);

        Assert.DoesNotContain(_log.Warnings.Concat(_log.Infos), line => line.Contains("secret") || line.Contains("Personal website"));
        Assert.DoesNotContain(signal.Holders, holder => holder.Label.Contains("secret") || holder.Detail.Contains("secret"));
    }

    private sealed class Files : ISessionFiles
    {
        public List<string> Texts { get; } = [];

        public IReadOnlyList<string> Read(string folder) => Texts;
    }

    private sealed class Table : IProcessTable
    {
        private readonly Dictionary<int, DateTime> _starts = [];

        public void Start(int pid, DateTime start) => _starts[pid] = start;

        public IReadOnlyList<ProcessEntry> List() => _starts.Keys.Select(pid => new ProcessEntry(pid, 1, "claude")).ToList();

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            cpu = TimeSpan.Zero;
            return _starts.TryGetValue(pid, out startUtc);
        }
    }

    private sealed class ListLog : ILog
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }
}
