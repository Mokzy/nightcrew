using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Appends to a text file, rolling it to .1 past maxBytes. Never throws on I/O or access errors: the line is dropped.</summary>
public sealed class FileLog(string path, long maxBytes = 5 * 1024 * 1024) : ILog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    var old = path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down: a locked, full or unwritable log loses the line.
            }
        }
    }
}
