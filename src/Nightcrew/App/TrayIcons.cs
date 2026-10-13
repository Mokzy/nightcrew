using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Nightcrew.App;

/// <summary>Three 16x16 dots drawn at runtime, so the exe needs no .ico files.</summary>
public sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private bool _disposed;

    public Icon Idle { get; } = Dot(Color.FromArgb(150, 150, 150));
    public Icon Live { get; } = Dot(Color.FromArgb(46, 160, 67));
    public Icon LidClosed { get; } = Dot(Color.FromArgb(47, 129, 247));

    private static Icon Dot(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 2, 2, 12, 12);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Icon.FromHandle does not own its handle, so each one is destroyed here.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var icon in new[] { Idle, Live, LidClosed })
        {
            var handle = icon.Handle;
            icon.Dispose();
            DestroyIcon(handle);
        }
    }
}
