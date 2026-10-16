using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void Version_is_0_3_0() => Assert.Equal("0.3.0", AppVersion.Text);
}
