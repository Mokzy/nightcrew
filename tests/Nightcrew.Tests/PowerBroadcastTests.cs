using System.Runtime.InteropServices;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PowerBroadcastTests
{
    private static readonly Guid LidSwitch = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    // POWERBROADCAST_SETTING: Guid @0, DataLength @16, Data @20.
    private static LidState Parse(Guid setting, int dataLength, int data)
    {
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(setting.ToByteArray(), 0, buffer, 16);
            Marshal.WriteInt32(buffer, 16, dataLength);
            Marshal.WriteInt32(buffer, 20, data);
            return PowerBroadcast.ParseLid(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(0, LidState.Closed)]
    [InlineData(1, LidState.Open)]
    [InlineData(2, LidState.Unknown)]
    public void Lid_switch_data_maps_to_lid_state(int data, LidState expected)
    {
        Assert.Equal(expected, Parse(LidSwitch, 4, data));
    }

    [Fact]
    public void Other_power_setting_is_unknown()
    {
        Assert.Equal(LidState.Unknown, Parse(Guid.NewGuid(), 4, 0));
    }

    [Fact]
    public void Short_data_is_unknown()
    {
        Assert.Equal(LidState.Unknown, Parse(LidSwitch, 0, 0));
    }

    [Fact]
    public void Null_pointer_is_unknown()
    {
        Assert.Equal(LidState.Unknown, PowerBroadcast.ParseLid(IntPtr.Zero));
    }

    [Fact]
    public void Power_capabilities_layout_matches_winnt()
    {
        Assert.Equal(76, Marshal.SizeOf<SystemPower.SystemPowerCapabilities>());
        Assert.Equal(6, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("SystemS4").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("HiberFilePresent").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("BatteryScale").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("DefaultLowLatencyWake").ToInt32());
    }

    [Fact]
    public void System_power_read_only_properties_do_not_throw()
    {
        var power = new SystemPower(() => IntPtr.Zero);
        Assert.True(power.Source is PowerSource.AC or PowerSource.DC);
        Assert.True(power.BatteryPercent is null or (>= 0 and <= 100));
        Assert.True(power.IdleTime >= TimeSpan.Zero);
        _ = power.HibernateAvailable;
    }
}
