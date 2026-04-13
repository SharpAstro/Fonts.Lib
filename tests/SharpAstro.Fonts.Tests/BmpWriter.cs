namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Minimal 8-bit grayscale BMP writer for visual test dumps. Not part of the
/// shipping library.
/// </summary>
internal static class BmpWriter
{
    public static void WriteGray8(string path, byte[] alpha, int width, int height)
    {
        // BMP rows are bottom-up and padded to 4 bytes.
        var rowSize = (width + 3) & ~3;
        var pixelBytes = rowSize * height;
        var paletteBytes = 256 * 4;
        var pixelOffset = 14 + 40 + paletteBytes;
        var fileSize = pixelOffset + pixelBytes;

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        // BITMAPFILEHEADER (14 bytes)
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(fileSize);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(pixelOffset);
        // BITMAPINFOHEADER (40 bytes)
        w.Write(40);                 // biSize
        w.Write(width);              // biWidth
        w.Write(height);             // biHeight (positive = bottom-up)
        w.Write((ushort)1);          // biPlanes
        w.Write((ushort)8);          // biBitCount
        w.Write(0);                  // biCompression
        w.Write(pixelBytes);         // biSizeImage
        w.Write(2835); w.Write(2835); // ~72 DPI
        w.Write(256); w.Write(256);  // biClrUsed / biClrImportant
        // Palette: 256 grayscale entries (B, G, R, 0)
        for (var i = 0; i < 256; i++)
        {
            w.Write((byte)i); w.Write((byte)i); w.Write((byte)i); w.Write((byte)0);
        }
        // Pixels (bottom-up rows, padded)
        var pad = new byte[rowSize - width];
        for (var y = height - 1; y >= 0; y--)
        {
            fs.Write(alpha, y * width, width);
            if (pad.Length > 0) fs.Write(pad, 0, pad.Length);
        }
    }
}
