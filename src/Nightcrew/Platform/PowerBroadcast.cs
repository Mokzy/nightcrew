using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class PowerBroadcast
{
    internal static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    /// <summary>Reads a POWERBROADCAST_SETTING (Guid @0, DataLength @16, Data @20) for the lid switch.</summary>
    public static LidState ParseLid(IntPtr powerBroadcastSetting)
    {
        if (powerBroadcastSetting == IntPtr.Zero) return LidState.Unknown;
        if (Marshal.PtrToStructure<Guid>(powerBroadcastSetting) != LidSwitchStateChange) return LidState.Unknown;
        if (Marshal.ReadInt32(powerBroadcastSetting, 16) < 4) return LidState.Unknown;
        return Marshal.ReadInt32(powerBroadcastSetting, 20) switch
        {
            0 => LidState.Closed,
            1 => LidState.Open,
            _ => LidState.Unknown,
        };
    }
}
