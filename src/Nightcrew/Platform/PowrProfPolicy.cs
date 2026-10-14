using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Reads and writes the lid action and sleep-after values of the active power scheme.</summary>
public sealed class PowrProfPolicy : IPowerPolicy
{
    private const uint ACCESS_AC_POWER_SETTING_INDEX = 0;
    private const uint ACCESS_DC_POWER_SETTING_INDEX = 1;
    private const uint KEY_WRITE = 0x20006;

    private sealed record Setting(string Name, Guid Subgroup, Guid Id);

    private static readonly Setting Lid = new("lid action",
        new Guid("4f971e89-eebd-4455-a8de-9e59040e7347"), new Guid("5ca83367-6e45-459f-a27b-476b1d01c936"));

    private static readonly Setting Standby = new("sleep after",
        new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));

    public PowerValues Read() => ReadValues(ActiveScheme());

    public void Write(PowerValues values)
    {
        var scheme = ActiveScheme();
        WriteIndex(scheme, Lid, ac: true, values.LidAc);
        WriteIndex(scheme, Lid, ac: false, values.LidDc);
        WriteIndex(scheme, Standby, ac: true, values.StandbyAc);
        WriteIndex(scheme, Standby, ac: false, values.StandbyDc);
        Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme");
        var readBack = ReadValues(scheme);
        if (readBack != values)
            throw new InvalidOperationException($"Power settings read-back mismatch: wrote {values}, read {readBack}");
    }

    public bool CanWrite(out string reason)
    {
        foreach (var setting in new[] { Lid, Standby })
        {
            foreach (var (access, side) in new[] { (ACCESS_AC_POWER_SETTING_INDEX, "AC"), (ACCESS_DC_POWER_SETTING_INDEX, "DC") })
            {
                var id = setting.Id;
                var code = PowerSettingAccessCheckEx(access, ref id, KEY_WRITE);
                if (code != 0)
                {
                    reason = $"policy blocks {setting.Name} {side} ({code})";
                    return false;
                }
            }
        }
        reason = "";
        return true;
    }

    private static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static PowerValues ReadValues(Guid scheme) => new(
        ReadIndex(scheme, Lid, ac: true),
        ReadIndex(scheme, Lid, ac: false),
        ReadIndex(scheme, Standby, ac: true),
        ReadIndex(scheme, Standby, ac: false));

    private static uint ReadIndex(Guid scheme, Setting setting, bool ac)
    {
        var subgroup = setting.Subgroup;
        var id = setting.Id;
        uint value;
        var code = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, out value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, out value);
        Check(code, ac ? "PowerReadACValueIndex" : "PowerReadDCValueIndex");
        return value;
    }

    private static void WriteIndex(Guid scheme, Setting setting, bool ac, uint value)
    {
        var subgroup = setting.Subgroup;
        var id = setting.Id;
        var code = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, value);
        Check(code, ac ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex");
    }

    private static void Check(uint code, string call)
    {
        if (code != 0) throw new InvalidOperationException($"{call} failed ({code})");
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingAccessCheckEx(uint accessFlags, ref Guid powerGuid, uint accessType);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
