using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The committed .ico is made by build/render-assets.ps1; this catches a stale one.</summary>
public sealed class AppIconFileTests
{
    [Fact]
    public void The_committed_app_icon_matches_the_sizes_the_geometry_makes()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Nightcrew.slnx"))) root = Path.GetDirectoryName(root.TrimEnd('\\'))!;
        var sizes = IcoSizes(File.ReadAllBytes(Path.Combine(root, "src", "Nightcrew", "nightcrew.ico")));
        Assert.Equal(MarkGeometry.AppIconSizes, sizes);
    }

    private static List<int> IcoSizes(byte[] ico) =>
        Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(i => ico[6 + 16 * i] is 0 ? 256 : (int)ico[6 + 16 * i]).ToList();
}
