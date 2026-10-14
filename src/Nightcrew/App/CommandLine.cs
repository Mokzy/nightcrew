namespace Nightcrew.App;

public sealed record ParsedArgs(bool Watchdog, bool Uninstall, bool DryRun);

/// <summary>The three verbs; anything else on the command line is ignored.</summary>
public static class CommandLine
{
    public static ParsedArgs Parse(string[] args)
    {
        bool Has(string verb) => args.Any(arg => string.Equals(arg, verb, StringComparison.OrdinalIgnoreCase));
        return new ParsedArgs(Has("--watchdog"), Has("--uninstall"), Has("--dry-run"));
    }
}
