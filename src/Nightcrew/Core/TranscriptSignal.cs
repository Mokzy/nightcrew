namespace Nightcrew.Core;

public interface IFileScanner
{
    /// <summary>Full path -> length in bytes of every *.jsonl under the roots, recursive.</summary>
    IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots);
}

/// <summary>Busy when an agent transcript grew or appeared since the previous sample.</summary>
public sealed class TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains) : IBusySignal
{
    private IReadOnlyDictionary<string, long>? _previous;

    public string Name => "transcripts";

    /// <summary>The file that made the most recent busy sample busy.</summary>
    public string? LastChangedPath { get; private set; }

    public bool Sample(TimeSpan now)
    {
        var previous = _previous;
        var current = scanner.Scan(roots);
        _previous = current;
        if (previous is null) return false;

        foreach (var file in current)
        {
            if (IsIgnored(file.Key)) continue;
            if (!previous.TryGetValue(file.Key, out var before) || file.Value > before)
            {
                LastChangedPath = file.Key;
                return true;
            }
        }
        return false;
    }

    private bool IsIgnored(string path) =>
        ignorePathContains.Any(part => part.Length > 0 && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
}
