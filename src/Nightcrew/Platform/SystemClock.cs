using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class SystemClock : IClock
{
    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public TimeSpan Now
    {
        get
        {
            QueryUnbiasedInterruptTime(out var ticks);
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    public DateTime UtcNow => DateTime.UtcNow;
}
