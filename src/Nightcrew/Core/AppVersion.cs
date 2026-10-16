namespace Nightcrew.Core;

/// <summary>Nightcrew's version, "0.3.0", from the Version in Directory.Build.props.</summary>
public static class AppVersion
{
    public static string Text { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
