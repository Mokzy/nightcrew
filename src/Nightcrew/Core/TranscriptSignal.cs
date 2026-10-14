namespace Nightcrew.Core;

public interface IFileScanner
{
    /// <summary>Full path -> length in bytes of every *.jsonl under the roots, recursive.</summary>
    IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots);
}

/// <summary>Busy when an agent transcript grew or appeared since the previous sample.</summary>
public sealed class TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains,
    TimeSpan? holderWindow = null) : IBusySignal, IHolderSource
{
    private readonly TimeSpan _window = holderWindow ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, TimeSpan> _grewAt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, long>? _previous;

    public string Name => "transcripts";

    /// <summary>The file that made the most recent busy sample busy.</summary>
    public string? LastChangedPath { get; private set; }

    /// <summary>One holder per agent, counting the transcripts that grew within the holder window.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var previous = _previous;
        var current = scanner.Scan(roots);
        _previous = current;
        if (previous is null) return false;

        var busy = false;
        foreach (var file in current)
        {
            if (IsIgnored(file.Key)) continue;
            if (previous.TryGetValue(file.Key, out var before) && file.Value <= before) continue;
            if (!busy) LastChangedPath = file.Key;
            busy = true;
            _grewAt[file.Key] = now;
        }

        foreach (var stale in _grewAt.Where(pair => now - pair.Value > _window).Select(pair => pair.Key).ToList())
            _grewAt.Remove(stale);
        if (busy) Holders = BuildHolders();
        return busy;
    }

    private IReadOnlyList<Holder> BuildHolders() =>
        _grewAt.Keys
            .GroupBy(LabelFor)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Holder(group.Key, group.Count() == 1 ? "1 session" : $"{group.Count()} sessions"))
            .ToList();

    private string LabelFor(string path)
    {
        if (path.IndexOf(@"\.claude\", StringComparison.OrdinalIgnoreCase) >= 0) return "Claude Code";
        if (path.IndexOf(@"\.codex\", StringComparison.OrdinalIgnoreCase) >= 0) return "Codex";
        foreach (var root in roots)
        {
            var expanded = Environment.ExpandEnvironmentVariables(root).TrimEnd('\\', '/');
            if (expanded.Length > 0 && path.StartsWith(expanded, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(expanded);
        }
        return "Transcripts";
    }

    private bool IsIgnored(string path) =>
        ignorePathContains.Any(part => part.Length > 0 && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
}
