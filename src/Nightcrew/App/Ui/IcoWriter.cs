namespace Nightcrew.App.Ui;

/// <summary>Writes .ico files: 32-bit bitmap frames for small sizes, a PNG frame for 256 px.</summary>
public static class IcoWriter
{
    public sealed class Frame
    {
        private Frame(int size, byte[] data, bool isPng)
        {
            if (size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(size), size, "An .ico frame is 1 to 256 px.");
            if (!isPng && data.Length != size * size * 4) throw new ArgumentException("Expected size x size BGRA pixels.", nameof(data));
            Size = size;
            Data = data;
            IsPng = isPng;
        }

        public int Size { get; }
        public byte[] Data { get; }
        public bool IsPng { get; }

        /// <summary>Straight-alpha BGRA pixels, top row first.</summary>
        public static Frame Bitmap(int size, byte[] bgra) => new(size, bgra, isPng: false);

        public static Frame Png(int size, byte[] png) => new(size, png, isPng: true);
    }

    public static byte[] Write(IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("An .ico needs at least one frame.", nameof(frames));
        var images = frames.Select(frame => frame.IsPng ? frame.Data : Dib(frame.Size, frame.Data)).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A BITMAPINFOHEADER, the pixels bottom row first, then an all-clear AND mask (alpha does the work).</summary>
    private static byte[] Dib(int size, byte[] bgra)
    {
        var maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40); // header size
        writer.Write(size); // width
        writer.Write(size * 2); // height of pixels plus mask
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bits per pixel
        writer.Write(0); // BI_RGB
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var row = size - 1; row >= 0; row--) writer.Write(bgra, row * size * 4, size * 4);
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }
}
