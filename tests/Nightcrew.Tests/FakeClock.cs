using Nightcrew.Core;

namespace Nightcrew.Tests;

public sealed class FakeClock : IClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);
    public DateTime UtcNow { get; set; } = new(2026, 10, 13, 22, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by)
    {
        Now += by;
        UtcNow += by;
    }
}
