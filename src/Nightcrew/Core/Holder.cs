namespace Nightcrew.Core;

/// <summary>Something holding the laptop awake, as the panel shows it: "Claude Code" · "2 sessions".</summary>
public readonly record struct Holder(string Label, string Detail);

/// <summary>A signal that can say who made its most recent busy sample busy.</summary>
public interface IHolderSource
{
    /// <summary>Holders from the most recent busy sample; empty before the first one.</summary>
    IReadOnlyList<Holder> Holders { get; }
}

public static class AgentNames
{
    /// <summary>"claude" → "Claude Code", "codex" → "Codex"; any other process name is shown as it is.</summary>
    public static string Display(string processName) => processName.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => processName,
    };
}
