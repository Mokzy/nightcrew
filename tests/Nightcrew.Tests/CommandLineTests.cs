using Nightcrew.App;
using Xunit;

namespace Nightcrew.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_starts_the_tray()
    {
        Assert.Equal(new ParsedArgs(Watchdog: false, Uninstall: false, DryRun: false), CommandLine.Parse([]));
    }

    [Theory]
    [InlineData("--watchdog", true, false, false)]
    [InlineData("--uninstall", false, true, false)]
    [InlineData("--dry-run", false, false, true)]
    public void Each_verb_sets_its_flag(string arg, bool watchdog, bool uninstall, bool dryRun)
    {
        Assert.Equal(new ParsedArgs(watchdog, uninstall, dryRun), CommandLine.Parse([arg]));
    }

    [Fact]
    public void Verbs_ignore_case()
    {
        Assert.Equal(new ParsedArgs(true, false, true), CommandLine.Parse(["--WATCHDOG", "--Dry-Run"]));
    }

    [Fact]
    public void Unknown_arguments_are_ignored()
    {
        Assert.Equal(new ParsedArgs(false, false, true), CommandLine.Parse(["--verbose", "dry-run", "--dry-run", "x"]));
        Assert.Equal(new ParsedArgs(false, false, false), CommandLine.Parse(["/watchdog", "--uninstall=yes", ""]));
    }
}
