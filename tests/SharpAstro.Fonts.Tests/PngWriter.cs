namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Minimal RGBA PNG writer for visual color-glyph dumps. Hand-rolled because
/// StbImageSharp is decode-only; the encoded format is uncompressed (single
/// IDAT with no zlib compression, framed as a stored block) — large but
/// trivial to produce and read by any PNG decoder.
/// </summary>
internal static class PngWriter
{
    public static void WriteRgba(string path, byte[] rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("rgba length must equal width*height*4.");

        using var fs = File.Create(path);
        // PNG signature
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        // IHDR
        WriteChunk(fs, "IHDR", w =>
        {
            WriteUInt32Be(w, (uint)width);
            WriteUInt32Be(w, (uint)height);
            w.WriteByte(8);  // bit depth
            w.WriteByte(6);  // color type: RGBA
            w.WriteByte(0);  // compression
            w.WriteByte(0);  // filter
            w.WriteByte(0);  // interlace
        });
        // IDAT — uncompressed deflate ("stored" blocks)
        WriteChunk(fs, "IDAT", w =>
        {
            using var ms = new MemoryStream();
            // zlib header: 0x78 0x01 = no compression / level 0
            ms.WriteByte(0x78);
            ms.WriteByte(0x01);
            var rowSize = width * 4;
            // Build raw filtered data: each row prefixed by filter byte 0.
            var data = new byte[(rowSize + 1) * height];
            for (var y = 0; y < height; y++)
            {
                data[y * (rowSize + 1)] = 0;
                Buffer.BlockCopy(rgba, y * rowSize, data, y * (rowSize + 1) + 1, rowSize);
            }
            // Stored deflate blocks: header(0x00 or 0x01) + LEN(uint16 LE) + ~LEN + raw
            var pos = 0;
            while (pos < data.Length)
            {
                var chunk = Math.Min(0xFFFF, data.Length - pos);
                var isLast = pos + chunk == data.Length;
                ms.WriteByte((byte)(isLast ? 1 : 0));
                ms.WriteByte((byte)(chunk & 0xFF));
                ms.WriteByte((byte)((chunk >> 8) & 0xFF));
                ms.WriteByte((byte)(~chunk & 0xFF));
                ms.WriteByte((byte)((~chunk >> 8) & 0xFF));
                ms.Write(data, pos, chunk);
                pos += chunk;
            }
            // Adler-32 of raw filtered data goes at the END of the zlib stream.
            var adler = Adler32(data);
            WriteUInt32Be(ms, adler);
            ms.Position = 0;
            ms.CopyTo(w);
        });
        // IEND
        WriteChunk(fs, "IEND", _ => { });
    }

    private static void WriteChunk(Stream s, string type, Action<Stream> write)
    {
        using var body = new MemoryStream();
        write(body);
        var bodyBytes = body.ToArray();
        WriteUInt32Be(s, (uint)bodyBytes.Length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(bodyBytes);
        var crc = Crc32(typeBytes, bodyBytes);
        WriteUInt32Be(s, crc);
    }

    private static void WriteUInt32Be(Stream s, uint v)
    {
        s.WriteByte((byte)((v >> 24) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = MakeTable();
    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
