using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class SystemPower(Func<IntPtr> ownWindow) : ISystemPower
{
    private const uint WM_SYSCOMMAND = 0x112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>SYSTEM_POWER_CAPABILITIES from winnt.h (76 bytes); BOOLEAN fields are single bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerCapabilities
    {
        public byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5;
        public byte HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent;
        public byte ThermalControl, ProcessorThrottle, ProcessorMinThrottle, ProcessorMaxThrottle;
        public byte FastSystemS4, Hiberboot, WakeAlarmPresent, AoAc, DiskSpinDown, HiberFileType, AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
        public byte SystemBatteriesPresent, BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] BatteryScale; // 3 x {Granularity, Capacity}
        public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public PowerSource Source =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 0 ? PowerSource.DC : PowerSource.AC;

    public int? BatteryPercent
    {
        get
        {
            if (!GetSystemPowerStatus(out var status)) return null;
            if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255) return null;
            return status.BatteryLifePercent;
        }
    }

    public TimeSpan IdleTime
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }

    public bool HibernateAvailable =>
        GetPwrCapabilities(out var caps) && caps.SystemS4 != 0 && caps.HiberFilePresent != 0;

    public bool Hibernate()
    {
        if (!HibernateAvailable || !EnableShutdownPrivilege()) return false;
        return SetSuspendState(hibernate: true, force: false, wakeupEventsDisabled: false);
    }

    public void MonitorOff() => PostMessage(ownWindow(), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(MONITOR_OFF));

    public void LockWorkstation() => LockWorkStation();

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED otherwise
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
