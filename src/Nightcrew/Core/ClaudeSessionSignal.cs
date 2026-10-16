using System.Globalization;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace Nightcrew.Core;

/// <summary>Reads Claude Code's per-session status files.</summary>
public interface ISessionFiles
{
    /// <summary>The contents of up to 64 files named &lt;digits&gt;.json, each 64 KB or less; empty if the folder is missing.</summary>
    IReadOnlyList<string> Read(string folder);
}

/// <summary>
/// Busy while a running Claude Code session says it's working, from the small status file Claude Code keeps per
/// session: "busy" counts, and "waiting" (for an approval or an answer) counts with Stay awake for approvals on and the
/// lid closed. The format is undocumented, so anything unexpected counts as nothing and the other signals carry on.
/// Only the pid, its start time, the status and the folder are read, and none of them is logged or shown.
/// </summary>
public sealed class ClaudeSessionSignal(ISessionFiles files, IProcessTable processes, Settings settings, Func<LidState> lid, ILog log)
    : IBusySignal, IHolderSource
{
    private bool _warned;

    public string Name => "claude-status";

    /// <summary>"Claude Code" · "N sessions": the same words the transcript signal uses.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var waitingCounts = settings.StayAwakeForApprovals && lid() == LidState.Closed;
        var working = 0;
        var unreadable = false;
        foreach (var text in files.Read(settings.ClaudeSessionsDir))
        {
            var session = Parse(text);
            if (session is null)
            {
                unreadable = true;
                continue;
            }
            var counts = session.Status == "busy" || (session.Status == "waiting" && waitingCounts);
            if (counts && IsRunning(session) && !IsIgnored(session.Cwd)) working++;
        }
        if (unreadable && !_warned)
        {
            _warned = true;
            log.Warn("A Claude Code session status file couldn't be read; skipping it");
        }
        if (working == 0) return false;
        Holders = [new Holder("Claude Code", working == 1 ? "1 session" : $"{working} sessions")];
        return true;
    }

    private static SessionStatus? Parse(string text)
    {
        try
        {
            return Json.Deserialize<SessionStatus?>(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A file counts only for the process that wrote it: the pid must be alive with the same creation time.</summary>
    private bool IsRunning(SessionStatus session)
    {
        if (!long.TryParse(session.ProcStart, NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime)) return false;
        DateTime started;
        try
        {
            started = DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        return processes.TryGetTimes(session.Pid, out var start, out _) && start == started;
    }

    /// <summary>
    /// The transcript signal's filter. Claude Code names a session's transcript folder after its working folder with every
    /// other character turned into "-", so the filter is matched against that form too: claude-mem's observer sessions
    /// run in ".claude-mem\observer-sessions", whose transcripts sit under "...-claude-mem-observer-sessions".
    /// </summary>
    private bool IsIgnored(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        var asProjectFolder = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
        return settings.IgnorePathContains.Any(part => part.Length > 0
            && (cwd!.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0
                || asProjectFolder.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    [DataContract]
    private sealed class SessionStatus
    {
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "procStart")] public string? ProcStart { get; set; }
        [DataMember(Name = "status")] public string? Status { get; set; }
        [DataMember(Name = "cwd")] public string? Cwd { get; set; }
    }
}
