using System.Runtime.InteropServices;
using System.Text;

namespace Nightcrew.Platform;

public static class AtomicFile
{
    private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    private const uint MOVEFILE_WRITE_THROUGH = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string replacement, uint flags);

    /// <summary>Writes to a temp file, flushes it to disk, then swaps it in.</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        if (!MoveFileEx(temp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw new IOException($"MoveFileEx failed for {path}", Marshal.GetLastWin32Error());
    }
}
