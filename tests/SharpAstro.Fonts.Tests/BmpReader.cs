namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Minimal 8-bit grayscale BMP reader, the counterpart to <see cref="BmpWriter"/>.
/// Only handles the format we write — uncompressed paletted 8bpp, bottom-up.
/// </summary>
internal static class BmpReader
{
    public static (byte[] Pixels, int Width, int Height) ReadGray8(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException($"Not a BMP file: {path}");

        var pixelOffset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = BitConverter.ToInt32(bytes, 22);
        var bitCount = BitConverter.ToUInt16(bytes, 28);
        var compression = BitConverter.ToInt32(bytes, 30);
        if (bitCount != 8 || compression != 0)
            throw new InvalidDataException(
                $"Expected uncompressed 8bpp; got bitCount={bitCount}, compression={compression}");

        var bottomUp = height > 0;
        var h = Math.Abs(height);
        var rowSize = (width + 3) & ~3;

        var pixels = new byte[width * h];
        for (var y = 0; y < h; y++)
        {
            var srcRow = pixelOffset + (bottomUp ? (h - 1 - y) : y) * rowSize;
            Buffer.BlockCopy(bytes, srcRow, pixels, y * width, width);
        }
        return (pixels, width, h);
    }
}
