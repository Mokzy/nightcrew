using System.Text;
using System.Text.RegularExpressions;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Reads Claude Code's session status files: only names that are digits plus ".json" (never the .key files beside
/// them, which hold login tokens), each 64 KB or less, at most 64 of them. A file that disappears or can't be read is
/// skipped.
/// </summary>
public sealed class SessionFileReader : ISessionFiles
{
    public const int MaxFiles = 64;
    public const long MaxBytes = 65_536;

    private static readonly Regex StatusFileName = new(@"^\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<string> Read(string folder)
    {
        var texts = new List<string>();
        var dir = Environment.ExpandEnvironmentVariables(folder);
        try
        {
            if (!Directory.Exists(dir)) return texts;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.json"))
            {
                if (texts.Count >= MaxFiles) break;
                if (!StatusFileName.IsMatch(file.Name)) continue;
                try
                {
                    if (file.Length > MaxBytes) continue;
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    texts.Add(reader.ReadToEnd());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Claude Code rewrites and deletes these files all the time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The folder went away or can't be listed: no status this tick.
        }
        return texts;
    }
}
