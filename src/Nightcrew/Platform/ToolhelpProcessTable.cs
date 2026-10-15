using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class ToolhelpProcessTable : IProcessTable
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    public IReadOnlyList<ProcessEntry> List()
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue) return [];
        try
        {
            var result = new List<ProcessEntry>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                result.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, NameOf(entry.szExeFile)));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>Reads the times straight from the process; the Process class would walk the whole process list first.</summary>
    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
    {
        startUtc = default;
        cpu = default;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            // A process that has exited but is still held open by someone has an exit time.
            if (!GetProcessTimes(handle, out var created, out var exited, out var kernel, out var user) || exited != 0) return false;
            startUtc = DateTime.FromFileTimeUtc(created);
            cpu = TimeSpan.FromTicks(kernel + user);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string NameOf(string exeFile)
    {
        var name = exeFile.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name;
    }
}
