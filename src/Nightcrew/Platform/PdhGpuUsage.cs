using System.Globalization;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Per-process GPU utilization from the "GPU Engine" performance counters.</summary>
public sealed class PdhGpuUsage : IGpuUsage, IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    private static readonly IReadOnlyDictionary<int, double> Empty = new Dictionary<int, double>();

    private IntPtr _query;
    private IntPtr _counter;
    private bool _disabled;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>The first call primes the query and returns nothing. Never throws.</summary>
    public IReadOnlyDictionary<int, double> PercentByPid()
    {
        if (_disabled) return Empty;
        try
        {
            if (_query == IntPtr.Zero)
            {
                _disabled = !Open();
                if (!_disabled) PdhCollectQueryData(_query);
                return Empty;
            }
            return PdhCollectQueryData(_query) == 0 ? MaxByPid(ReadItems()) : Empty;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _disabled = true;
            return Empty;
        }
    }

    public void Dispose()
    {
        _disabled = true;
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private bool Open()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return false;
        }
        if (PdhAddEnglishCounter(_query, CounterPath, IntPtr.Zero, out _counter) == 0) return true;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        return false;
    }

    private List<(string Instance, double Percent)> ReadItems()
    {
        var items = new List<(string Instance, double Percent)>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        // Instances can appear between the size call and the read, so retry a few times.
        for (var attempt = 0; attempt < 3 && status == PDH_MORE_DATA; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out var count, buffer);
                if (status != 0) continue;
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                    if (item.FmtValue.CStatus > PDH_CSTATUS_NEW_DATA || item.Name == IntPtr.Zero) continue;
                    items.Add((Marshal.PtrToStringUni(item.Name), item.FmtValue.DoubleValue));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return items;
    }

    /// <summary>Keeps, for each pid, the maximum value across its engine instances.</summary>
    internal static Dictionary<int, double> MaxByPid(IEnumerable<(string Instance, double Percent)> items)
    {
        var result = new Dictionary<int, double>();
        foreach (var (instance, percent) in items)
        {
            if (!TryParsePid(instance, out var pid)) continue;
            if (!result.TryGetValue(pid, out var max) || percent > max) result[pid] = percent;
        }
        return result;
    }

    /// <summary>Parses 1234 from instance names like "pid_1234_luid_0x..._engtype_3D".</summary>
    internal static bool TryParsePid(string instance, out int pid)
    {
        pid = 0;
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return false;
        var end = instance.IndexOf('_', 4);
        var digits = end < 0 ? instance.Substring(4) : instance.Substring(4, end - 4);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }
}
