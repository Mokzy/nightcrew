namespace Nightcrew.Core;

public static class ProcessNames
{
    /// <summary>
    /// A pid's name from the tick's process list, which is already in memory, for the panel's holder rows; null when the
    /// process has gone. Opening the process for its name took 10–12 ms per busy GPU process every tick.
    /// </summary>
    public static Func<int, string?> From(IProcessTable table) => pid =>
    {
        foreach (var process in table.List())
            if (process.Pid == pid) return process.Name;
        return null;
    };
}
