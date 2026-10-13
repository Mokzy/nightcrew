using Xunit;

namespace Nightcrew.Tests;

public class ToolchainTests
{
    private sealed record Sample(int A, string B)
    {
        public required int C { get; init; }
    }

    [Fact]
    public void Modern_csharp_features_work_on_net48()
    {
        var sample = new Sample(1, "b") { C = 3 };
        int[] numbers = [1, 2, 3];
        Assert.Equal(3, sample.C);
        Assert.Equal(sample with { C = 3 }, sample);
        Assert.Equal(6, numbers.Sum());
    }
}
