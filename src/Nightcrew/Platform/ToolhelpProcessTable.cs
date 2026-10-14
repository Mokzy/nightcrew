using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class ToolhelpProcessTable : IProcessTable
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
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

    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            startUtc = process.StartTime.ToUniversalTime();
            cpu = process.TotalProcessorTime;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            startUtc = default;
            cpu = default;
            return false;
        }
    }

    private static string NameOf(string exeFile)
    {
        var name = exeFile.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name;
    }
}
