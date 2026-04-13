namespace SharpAstro.Fonts.Type1;

/// <summary>
/// Reads a PostScript Font Binary (.pfb) wrapper. Returns the concatenated
/// ASCII header (PostScript text) and the concatenated binary (eexec-encrypted)
/// payload.
///
/// <para>PFB layout: a sequence of segments. Each segment is
/// <c>0x80</c> + type-byte + uint32-LE length (omitted for type 3) + data.
/// Types: 1 = ASCII text, 2 = binary, 3 = EOF marker.</para>
/// </summary>
internal static class PfbReader
{
    public static (byte[] Ascii, byte[] Binary) Read(ReadOnlySpan<byte> pfb)
    {
        var ascii = new System.IO.MemoryStream();
        var binary = new System.IO.MemoryStream();
        var i = 0;
        while (i < pfb.Length)
        {
            if (pfb[i] != 0x80)
                throw new InvalidDataException($"PFB: missing 0x80 marker at offset {i}");
            var type = pfb[i + 1];
            if (type == 3) break;
            if (type is not (1 or 2))
                throw new InvalidDataException($"PFB: unknown segment type {type}");
            var len = pfb[i + 2] | (pfb[i + 3] << 8) | (pfb[i + 4] << 16) | (pfb[i + 5] << 24);
            var dataStart = i + 6;
            var slice = pfb.Slice(dataStart, len);
            if (type == 1) ascii.Write(slice);
            else binary.Write(slice);
            i = dataStart + len;
        }
        return (ascii.ToArray(), binary.ToArray());
    }

    /// <summary>
    /// Detect raw .pfa (ASCII Type 1) vs .pfb (binary wrapped). PFA starts
    /// with the literal "%!PS-AdobeFont" or "%!FontType1".
    /// </summary>
    public static bool IsPfb(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == 0x80 && data[1] is 1 or 2 or 3;
}
