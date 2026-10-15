using System.Collections.Concurrent;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Keeps the *.jsonl listing current from folder change notifications, so a scan doesn't walk thousands of
/// transcripts every tick. Each root is listed in full once, again every <see cref="RelistEvery"/> as a safety net,
/// and on the next scan after its watcher reports missed changes. A root that can't be watched (missing, or the
/// watcher failed) is listed in full on every scan, as before.
/// </summary>
public sealed class WatchingJsonlScanner(IFileScanner lister, IClock clock) : IFileScanner, IDisposable
{
    private static readonly TimeSpan RelistEvery = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Root> _roots = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Root
    {
        public FileSystemWatcher? Watcher;
        public Dictionary<string, long> Files = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan? ListedAt;
        public volatile bool Missed;
        public readonly ConcurrentQueue<string> Changed = new();
    }

    public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
    {
        var now = clock.Now;
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in roots)
        {
            if (!_roots.TryGetValue(name, out var root)) _roots[name] = root = new Root();
            if (root.Missed)
            {
                // A watcher that reported an error may have stopped for good (its folder was deleted, for example).
                root.Missed = false;
                root.Watcher?.Dispose();
                root.Watcher = null;
            }
            Watch(root, Environment.ExpandEnvironmentVariables(name));

            if (root.Watcher is null || root.ListedAt is not { } listedAt || now - listedAt >= RelistEvery)
            {
                // The watcher starts before the listing, so a change during the listing is queued, not lost.
                while (root.Changed.TryDequeue(out _)) { }
                root.Files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in lister.Scan([name])) root.Files[file.Key] = file.Value;
                root.ListedAt = now;
            }
            else
            {
                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (root.Changed.TryDequeue(out var path)) changed.Add(path);
                foreach (var path in changed) Refresh(root.Files, path);
            }

            foreach (var file in root.Files) result[file.Key] = file.Value;
        }
        return result;
    }

    /// <summary>What the watcher's Error event does: the next scan replaces the watcher and lists the root again.</summary>
    internal void ChangesMissed(string root)
    {
        if (_roots.TryGetValue(root, out var state)) state.Missed = true;
    }

    public void Dispose()
    {
        foreach (var root in _roots.Values) root.Watcher?.Dispose();
        _roots.Clear();
    }

    /// <summary>Starts a watcher when the folder exists, and drops one whose folder has gone.</summary>
    private static void Watch(Root root, string path)
    {
        var exists = Directory.Exists(path);
        if (root.Watcher is not null)
        {
            if (exists) return;
            root.Watcher.Dispose();
            root.Watcher = null;
            return;
        }
        if (!exists) return;
        try
        {
            var watcher = new FileSystemWatcher(path, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => root.Changed.Enqueue(e.FullPath);
            watcher.Changed += (_, e) => root.Changed.Enqueue(e.FullPath);
            watcher.Deleted += (_, e) => root.Changed.Enqueue(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                root.Changed.Enqueue(e.OldFullPath);
                root.Changed.Enqueue(e.FullPath);
            };
            watcher.Error += (_, _) => root.Missed = true;
            watcher.EnableRaisingEvents = true;
            root.Watcher = watcher;
            root.ListedAt = null; // files written before this watcher started are only found by listing
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not watchable: this root is listed in full on every scan instead.
        }
    }

    /// <summary>Reads one changed path's current length; a path that's gone, or no longer a transcript, is dropped.</summary>
    private static void Refresh(Dictionary<string, long> files, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (info.Exists && fullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            files[fullPath] = info.Length;
        else
            files.Remove(fullPath);
    }
}
