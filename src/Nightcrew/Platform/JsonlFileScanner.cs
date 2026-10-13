using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Lists *.jsonl files with their lengths; missing or unreadable roots are skipped.</summary>
public sealed class JsonlFileScanner : IFileScanner
{
    public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(root));
                if (!dir.Exists) continue;
                // Length comes from the find data, so no file is opened.
                foreach (var file in dir.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                    files[file.FullName] = file.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }
}
