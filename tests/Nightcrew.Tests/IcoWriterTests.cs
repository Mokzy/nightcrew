using System.Drawing;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class IcoWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Solid(int size, byte b, byte g, byte r, byte a) =>
        Enumerable.Range(0, size * size).SelectMany(_ => new[] { b, g, r, a }).ToArray();

    [Fact]
    public void An_ico_with_two_bitmap_frames_opens_in_System_Drawing_at_each_size()
    {
        var bytes = IcoWriter.Write([IcoWriter.Frame.Bitmap(16, Solid(16, 0, 0, 255, 255)), IcoWriter.Frame.Bitmap(32, Solid(32, 255, 0, 0, 255))]);

        Assert.Equal(2, BitConverter.ToUInt16(bytes, 4));
        using var small = new Icon(new MemoryStream(bytes), 16, 16);
        using var large = new Icon(new MemoryStream(bytes), 32, 32);
        Assert.Equal(16, small.Width);
        Assert.Equal(32, large.Width);

        using var bitmap = large.ToBitmap();
        Assert.Equal(Color.FromArgb(255, 0, 0, 255).ToArgb(), bitmap.GetPixel(5, 5).ToArgb());
    }

    [Fact]
    public void The_top_row_stays_on_top()
    {
        var pixels = Solid(16, 0, 0, 0, 0);
        for (var x = 0; x < 16; x++) pixels[x * 4 + 1] = pixels[x * 4 + 3] = 255; // top row green

        using var icon = new Icon(new MemoryStream(IcoWriter.Write([IcoWriter.Frame.Bitmap(16, pixels)])), 16, 16);
        using var bitmap = icon.ToBitmap();
        Assert.Equal(Color.FromArgb(255, 0, 255, 0).ToArgb(), bitmap.GetPixel(3, 0).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(3, 15).A);
    }

    [Fact]
    public void A_256_frame_is_stored_as_png_with_a_zero_size_byte()
    {
        var png = PngSignature.Concat(new byte[20]).ToArray();
        var bytes = IcoWriter.Write([IcoWriter.Frame.Png(256, png)]);

        Assert.Equal(0, bytes[6]); // width 0 means 256
        Assert.Equal(0, bytes[7]);
        var offset = BitConverter.ToInt32(bytes, 6 + 12);
        Assert.Equal(PngSignature, bytes.Skip(offset).Take(8));
    }

    [Fact]
    public void Bad_frames_are_refused()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Frame.Bitmap(16, new byte[10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoWriter.Frame.Png(300, PngSignature));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
    }
}
