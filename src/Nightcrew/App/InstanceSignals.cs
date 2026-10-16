namespace Nightcrew.App;

/// <summary>Named events another start of nightcrew.exe uses to reach the running copy.</summary>
public static class InstanceSignals
{
    public const string Instance = @"Local\Nightcrew.Instance";

    /// <summary>A second start: the running copy opens its panel.</summary>
    public const string ShowPanel = @"Local\Nightcrew.ShowPanel";

    /// <summary>nightcrew --exit: the running copy quits, putting everything back as Quit does.</summary>
    public const string Exit = @"Local\Nightcrew.Exit";

    /// <summary>Sets the named event if the running copy made it; false when Nightcrew isn't running.</summary>
    public static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }
}
