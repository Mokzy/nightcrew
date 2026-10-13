using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

// Read-only checks against the real machine. Never call Write() here.
public sealed class PowrProfPolicyTests
{
    [Fact]
    public void Read_returns_lid_actions_in_range()
    {
        var values = new PowrProfPolicy().Read();

        Assert.InRange(values.LidAc, (uint)LidAction.DoNothing, (uint)LidAction.ShutDown);
        Assert.InRange(values.LidDc, (uint)LidAction.DoNothing, (uint)LidAction.ShutDown);
    }

    [Fact]
    public void CanWrite_returns_without_throwing()
    {
        var allowed = new PowrProfPolicy().CanWrite(out var reason);

        if (allowed) Assert.Equal("", reason);
        else Assert.StartsWith("policy blocks ", reason);
    }
}
