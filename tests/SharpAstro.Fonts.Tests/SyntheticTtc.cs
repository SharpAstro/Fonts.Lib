namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Builds a spec-conformant TrueType Collection in memory out of standalone SFNT faces.
/// The repo ships no real TTC fixture, so collection behaviour is tested against one we
/// synthesize — which keeps that coverage on every platform rather than only on Windows
/// boxes that happen to have cambria.ttc.
/// </summary>
internal static class SyntheticTtc
{
    /// <summary>
    /// Build a synthetic TTC wrapping <paramref name="faces"/> with a TTC v1
    /// header. Each input face is a complete standalone SFNT byte sequence;
    /// when we splat it into the TTC at a non-zero offset, we need to rewrite
    /// the table record offsets in its offset table to be absolute (i.e.,
    /// add the placement offset). Real TTCs have absolute offsets — so this
    /// fixup is exactly what produces a spec-conformant collection.
    /// </summary>
    public static byte[] Build(int majorVersion, byte[][] faces)
    {
        // Header: 4 (ttcf) + 2 (major) + 2 (minor) + 4 (numFonts) + 4 * numFonts (offsets)
        // v2 adds: 4 (dsigTag) + 4 (dsigLength) + 4 (dsigOffset) — all zero here.
        var headerSize = 4 + 2 + 2 + 4 + 4 * faces.Length + (majorVersion == 2 ? 12 : 0);
        var totalSize = headerSize;
        var faceOffsets = new uint[faces.Length];
        for (var i = 0; i < faces.Length; i++)
        {
            // Each face starts at an 8-byte-aligned offset (TTC convention).
            var aligned = (totalSize + 7) & ~7;
            faceOffsets[i] = (uint)aligned;
            totalSize = aligned + faces[i].Length;
        }

        var buf = new byte[totalSize];
        var pos = 0;
        buf[pos++] = 0x74; buf[pos++] = 0x74; buf[pos++] = 0x63; buf[pos++] = 0x66;
        buf[pos++] = 0; buf[pos++] = (byte)majorVersion;
        buf[pos++] = 0; buf[pos++] = 0;
        var num = (uint)faces.Length;
        buf[pos++] = (byte)(num >> 24); buf[pos++] = (byte)(num >> 16);
        buf[pos++] = (byte)(num >> 8);  buf[pos++] = (byte)num;
        foreach (var off in faceOffsets)
        {
            buf[pos++] = (byte)(off >> 24); buf[pos++] = (byte)(off >> 16);
            buf[pos++] = (byte)(off >> 8);  buf[pos++] = (byte)off;
        }
        if (majorVersion == 2) pos += 12;

        // Splat each face at its declared offset, then rewrite its table
        // record offsets to be absolute within the TTC.
        for (var i = 0; i < faces.Length; i++)
        {
            var dst = (int)faceOffsets[i];
            Buffer.BlockCopy(faces[i], 0, buf, dst, faces[i].Length);
            FixupOffsetTable(buf.AsSpan(dst), addToOffsets: dst);
        }
        return buf;
    }

    /// <summary>
    /// Add <paramref name="addToOffsets"/> to every table record offset in
    /// the SFNT-style offset table at the start of <paramref name="face"/>.
    /// The offset table layout is:
    ///   uint32 sfntVersion, uint16 numTables, 6 bytes searchRange/etc.,
    ///   numTables × { Tag(4), checksum(4), offset(4), length(4) }.
    /// We patch only the offset field of each record; everything else stays.
    /// </summary>
    private static void FixupOffsetTable(Span<byte> face, int addToOffsets)
    {
        // Skip uint32 sfntVersion.
        var numTables = (face[4] << 8) | face[5];
        // Records start after sfntVersion(4) + numTables(2) + searchRange(2)
        //                  + entrySelector(2) + rangeShift(2) = 12 bytes.
        var pos = 12;
        for (var t = 0; t < numTables; t++)
        {
            // record = tag(4) + checksum(4) + offset(4) + length(4)
            var off = (uint)((face[pos + 8] << 24) | (face[pos + 9] << 16) |
                             (face[pos + 10] << 8) | face[pos + 11]);
            var newOff = off + (uint)addToOffsets;
            face[pos + 8]  = (byte)(newOff >> 24);
            face[pos + 9]  = (byte)(newOff >> 16);
            face[pos + 10] = (byte)(newOff >> 8);
            face[pos + 11] = (byte)newOff;
            pos += 16;
        }
    }
}
