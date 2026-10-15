using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The dry run, and the GPU holder pids the planner leaves alone.</summary>
public sealed class DryRunEfficiencyTests
{
    private readonly ListLog _log = new();

    private sealed class ListLog : ILog
    {
        public List<string> Infos { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) { }
    }

    private static AppFamily Family() => new("Windows Command Processor", "cmd", [new ProcessRef(1234, new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc))]);

    [Fact]
    public void The_dry_run_logs_what_it_would_do_and_keeps_track_like_the_real_thing()
    {
        var dryRun = new DryRunEfficiency(_log);

        Assert.Equal(ApplyResult.Applied, dryRun.Apply(Family(), dryRun.Generation));
        Assert.Equal(ApplyResult.Applied, dryRun.Apply(Family(), dryRun.Generation));
        Assert.Equal([Family().Root], dryRun.Applied);
        Assert.Equal(1, dryRun.RestoreAll("lid opened"));
        Assert.Empty(dryRun.Applied);

        Assert.Equal(["[dry run] would put into Efficiency mode: Windows Command Processor (cmd, 1 process)",
            "[dry run] would end Efficiency mode for 1 app: lid opened"], _log.Infos);
    }

    [Fact]
    public void A_dry_run_pass_that_began_before_a_restore_changes_nothing()
    {
        var dryRun = new DryRunEfficiency(_log);
        var generation = dryRun.Generation;
        dryRun.RestoreAll("lid opened");

        Assert.Equal(ApplyResult.Stale, dryRun.Apply(Family(), generation));
        Assert.Empty(dryRun.Applied);
    }

    [Fact]
    public void A_dry_run_restore_of_one_app_is_logged_with_its_name()
    {
        var dryRun = new DryRunEfficiency(_log);
        dryRun.Apply(Family(), dryRun.Generation);

        dryRun.Restore(Family().Root, "left alone now");

        Assert.Empty(dryRun.Applied);
        Assert.Equal("[dry run] would end Efficiency mode for Windows Command Processor: left alone now", _log.Infos.Last());
    }

    private sealed class FakeGpu(Dictionary<int, double> percent) : IGpuUsage
    {
        public IReadOnlyDictionary<int, double> PercentByPid() => percent;
    }

    [Fact]
    public void The_gpu_signal_reports_the_pids_of_its_busy_processes_busiest_first()
    {
        var signal = new GpuSignal(new FakeGpu(new() { [10] = 50, [11] = 5, [12] = 80 }), 30);

        Assert.True(signal.Sample(TimeSpan.Zero));

        Assert.Equal([12, 10], signal.BusyPids);
    }
}
