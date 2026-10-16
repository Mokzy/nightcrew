using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class GpuSignalTests
{
    private static readonly TimeSpan Now = TimeSpan.FromHours(1);

    private sealed class FakeGpu : IGpuUsage
    {
        public Dictionary<int, double> Values { get; set; } = [];
        public IReadOnlyDictionary<int, double> PercentByPid() => Values;
    }

    [Fact]
    public void Name_is_gpu() => Assert.Equal("gpu", new GpuSignal(new FakeGpu(), 30).Name);

    [Fact]
    public void No_gpu_processes_is_not_busy()
    {
        var signal = new GpuSignal(new FakeGpu(), 30);
        Assert.False(signal.Sample(Now));
        Assert.Null(signal.LastBusyPid);
    }

    [Fact]
    public void Below_threshold_is_not_busy()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 29.9, [20] = 5 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.False(signal.Sample(Now));
        Assert.Null(signal.LastBusyPid);
    }

    [Fact]
    public void At_threshold_is_busy_and_records_the_pid()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 2, [42] = 30 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        Assert.Equal(42, signal.LastBusyPid);
    }

    [Fact]
    public void Busiest_pid_is_recorded_when_several_are_busy()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 50, [20] = 90, [30] = 35 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        Assert.Equal(20, signal.LastBusyPid);
    }

    [Fact]
    public void Last_busy_pid_is_kept_after_the_gpu_goes_quiet()
    {
        var gpu = new FakeGpu { Values = new() { [7] = 80 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        gpu.Values = new() { [7] = 1 };
        Assert.False(signal.Sample(Now + TimeSpan.FromSeconds(10)));
        Assert.Equal(7, signal.LastBusyPid);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000C2A1_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("pid_8_luid_0x00000000_0x0000C2A1_phys_0_eng_3_engtype_VideoDecode", 8)]
    [InlineData("pid_77", 77)]
    public void Parses_the_pid_from_an_instance_name(string instance, int expected)
    {
        Assert.True(PdhGpuUsage.TryParsePid(instance, out var pid));
        Assert.Equal(expected, pid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid__luid_0x0")]
    [InlineData("pid_abc_luid_0x0")]
    [InlineData("pid_-5_luid_0x0")]
    [InlineData("luid_0x0_pid_12")]
    public void Rejects_instance_names_without_a_pid(string instance)
    {
        Assert.False(PdhGpuUsage.TryParsePid(instance, out _));
    }

    [Fact]
    public void Each_pid_gets_its_busiest_engine()
    {
        var result = PdhGpuUsage.MaxByPid(
        [
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 12.5),
            ("pid_1_luid_0x0_0x1_phys_0_eng_1_engtype_Copy", 40),
            ("pid_1_luid_0x0_0x1_phys_0_eng_2_engtype_VideoDecode", 7),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 3),
            ("not_a_pid_instance", 99),
        ]);
        Assert.Equal(2, result.Count);
        Assert.Equal(40, result[1]);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Real_counters_prime_first_and_never_throw()
    {
        using var gpu = new PdhGpuUsage();
        Assert.Empty(gpu.PercentByPid());
        Thread.Sleep(250);
        var second = gpu.PercentByPid();
        Assert.All(second.Values, value => Assert.InRange(value, 0.0, 100.0));
        gpu.Dispose();
        Assert.Empty(gpu.PercentByPid());
    }

    [Fact]
    public void Holder_names_come_from_the_tick_list()
    {
        var name = ProcessNames.From(new ListTable([new ProcessEntry(42, 1, "ollama")]));

        Assert.Equal("ollama", name(42));
        Assert.Null(name(43));
    }

    private sealed class ListTable(IReadOnlyList<ProcessEntry> entries) : IProcessTable
    {
        public IReadOnlyList<ProcessEntry> List() => entries;

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = default;
            cpu = default;
            return false;
        }
    }
}
