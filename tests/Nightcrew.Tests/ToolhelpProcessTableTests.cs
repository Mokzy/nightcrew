using System.Diagnostics;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ToolhelpProcessTableTests
{
    private readonly ToolhelpProcessTable _table = new();

    [Fact]
    public void Reads_the_start_time_and_cpu_of_a_running_process()
    {
        using var self = Process.GetCurrentProcess();
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 50) { } // burn some CPU so there is something to read
        var cpuBefore = self.TotalProcessorTime;

        Assert.True(_table.TryGetTimes(self.Id, out var startUtc, out var cpu));

        self.Refresh();
        Assert.Equal(DateTimeKind.Utc, startUtc.Kind);
        Assert.InRange(startUtc, self.StartTime.ToUniversalTime().AddSeconds(-1), self.StartTime.ToUniversalTime().AddSeconds(1));
        Assert.InRange(cpu, cpuBefore, self.TotalProcessorTime);
    }

    [Fact]
    public void An_exited_process_is_not_read_even_while_a_handle_keeps_it_around()
    {
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        exited.WaitForExit();

        Assert.False(_table.TryGetTimes(exited.Id, out _, out _));
    }

    [Fact]
    public void A_pid_with_no_process_is_not_read()
    {
        Assert.False(_table.TryGetTimes(0x7FFFFFFC, out _, out _));
    }

    [Fact]
    public void Reading_another_process_does_not_walk_every_process_on_the_machine()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.True(_table.TryGetTimes(child.Id, out _, out _));

            // A direct read takes about 15 microseconds; the Process class takes about 180, because it walks the process
            // list first. The fastest of five batches is used, so a scheduler hiccup or GC pause in one batch can't fail it.
            var fastest = long.MaxValue;
            for (var batch = 0; batch < 5; batch++)
            {
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++) _table.TryGetTimes(child.Id, out _, out _);
                fastest = Math.Min(fastest, timer.ElapsedMilliseconds);
            }
            Assert.InRange(fastest, 0, 15);
        }
        finally
        {
            child.Kill();
        }
    }
}
