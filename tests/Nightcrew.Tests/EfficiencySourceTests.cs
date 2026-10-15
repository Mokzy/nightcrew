using System.Diagnostics;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The planner's snapshot from Windows, and display names.</summary>
public sealed class EfficiencySourceTests : IDisposable
{
    private Process? _child;

    public void Dispose()
    {
        try { if (_child is { HasExited: false }) _child.Kill(); } catch (InvalidOperationException) { }
        _child?.Dispose();
    }

    [Fact]
    public void The_snapshot_has_this_process_with_its_session_and_start_time()
    {
        using var self = Process.GetCurrentProcess();
        var table = new ToolhelpProcessTable();

        var snapshot = new EfficiencySource(table).Take([42]);

        var me = Assert.Single(snapshot.Processes, p => p.Pid == self.Id);
        Assert.Equal(self.SessionId, me.Session);
        Assert.Equal(self.SessionId, snapshot.Session);
        Assert.True(table.TryGetTimes(self.Id, out var start, out _));
        Assert.Equal(start, me.StartUtc);
        Assert.Equal([42], snapshot.HolderPids);
    }

    [Fact]
    public void The_display_name_is_the_file_description()
    {
        _child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(new ToolhelpProcessTable().TryGetTimes(_child.Id, out var start, out _));
        var source = new EfficiencySource(new ToolhelpProcessTable());

        Assert.Equal("Windows Command Processor", source.DisplayName(new ProcessInfo(_child.Id, 0, "cmd", start, 1)));
    }

    [Fact]
    public void Without_a_description_the_display_name_is_the_process_name()
    {
        var source = new EfficiencySource(new ToolhelpProcessTable());

        Assert.Equal("gone", source.DisplayName(new ProcessInfo(0x7FFFFFFC, 0, "gone", DateTime.UtcNow, 1)));
    }
}
