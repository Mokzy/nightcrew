namespace Nightcrew.Core;

/// <summary>The two tray marks. The user picks one with the <c>Mark</c> setting.</summary>
public enum MarkKind { Lantern, MoonAndStar }

public static class MarkKinds
{
    public const string LanternName = "lantern";
    public const string MoonName = "moon";

    /// <summary>"moon" picks the moon and star; anything else, including a typo, is the lantern.</summary>
    public static MarkKind Parse(string? value) =>
        string.Equals(value?.Trim(), MoonName, StringComparison.OrdinalIgnoreCase) ? MarkKind.MoonAndStar : MarkKind.Lantern;

    public static string Name(MarkKind kind) => kind == MarkKind.MoonAndStar ? MoonName : LanternName;
}
