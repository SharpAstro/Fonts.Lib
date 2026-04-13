using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cblc;

/// <summary>
/// Image data formats stored in CBDT (only PNG-bearing variants are
/// implemented for Phase 6 — the monochrome packed-bit formats from EBDT
/// land later if needed).
/// </summary>
public enum BitmapImageFormat
{
    /// <summary>Format 17: small metrics, PNG data.</summary>
    SmallMetricsPng = 17,
    /// <summary>Format 18: big metrics, PNG data.</summary>
    BigMetricsPng = 18,
    /// <summary>Format 19: PNG data only — metrics taken from CBLC index.</summary>
    PngOnly = 19,
}

/// <summary>
/// One bitmap "strike" — a set of glyph bitmaps at one PPEM size.
/// </summary>
public sealed class BitmapStrike
{
    public ushort PpemX { get; }
    public ushort PpemY { get; }
    public ushort StartGlyphIndex { get; }
    public ushort EndGlyphIndex { get; }
    /// <summary>Each entry: (firstGid, lastGid, indexSubtable). Sorted by firstGid.</summary>
    public IndexSubtable[] IndexSubtables { get; }

    public BitmapStrike(ushort ppemX, ushort ppemY, ushort startGid, ushort endGid,
        IndexSubtable[] subtables)
    {
        PpemX = ppemX;
        PpemY = ppemY;
        StartGlyphIndex = startGid;
        EndGlyphIndex = endGid;
        IndexSubtables = subtables;
    }

    /// <summary>Find the index subtable covering <paramref name="gid"/>, or null.</summary>
    public IndexSubtable? FindSubtable(uint gid)
    {
        foreach (var s in IndexSubtables)
            if (gid >= s.FirstGlyphIndex && gid <= s.LastGlyphIndex)
                return s;
        return null;
    }
}

/// <summary>
/// One CBLC IndexSubTable. Currently we read enough to locate a glyph's
/// data within CBDT — formats 1, 2, 3 are supported (the variants seen in
/// real-world emoji fonts; format 4 / 5 sparse layouts deferred).
/// </summary>
public sealed class IndexSubtable
{
    public ushort FirstGlyphIndex { get; }
    public ushort LastGlyphIndex { get; }
    public ushort IndexFormat { get; }
    public BitmapImageFormat ImageFormat { get; }
    /// <summary>Offset within CBDT where this subtable's images start.</summary>
    public uint ImageDataOffset { get; }

    /// <summary>Format 1/3: sparse offsets per glyph (one extra sentinel for end). Null for fixed-size formats.</summary>
    public uint[]? Offsets { get; }
    /// <summary>Format 2 only: constant image size (each glyph's image is exactly this many bytes).</summary>
    public uint ConstImageSize { get; }
    /// <summary>Format 2 only: shared big metrics (8 bytes, raw).</summary>
    public byte[]? ConstBigMetrics { get; }

    public IndexSubtable(ushort first, ushort last, ushort indexFormat,
        BitmapImageFormat imageFormat, uint imageDataOffset,
        uint[]? offsets, uint constImageSize, byte[]? constBigMetrics)
    {
        FirstGlyphIndex = first;
        LastGlyphIndex = last;
        IndexFormat = indexFormat;
        ImageFormat = imageFormat;
        ImageDataOffset = imageDataOffset;
        Offsets = offsets;
        ConstImageSize = constImageSize;
        ConstBigMetrics = constBigMetrics;
    }

    /// <summary>
    /// Compute the byte range within CBDT for <paramref name="gid"/>'s image.
    /// Returns (offset, length) or (0, 0) if not present.
    /// </summary>
    public (uint Offset, uint Length) LocateImage(uint gid)
    {
        if (gid < FirstGlyphIndex || gid > LastGlyphIndex) return (0, 0);
        var localIdx = (int)(gid - FirstGlyphIndex);

        switch (IndexFormat)
        {
            case 1: // uint32 offsets
            case 3: // uint16 offsets
            {
                if (Offsets is null || localIdx + 1 >= Offsets.Length) return (0, 0);
                var start = Offsets[localIdx];
                var end = Offsets[localIdx + 1];
                if (end <= start) return (0, 0);
                return (ImageDataOffset + start, end - start);
            }
            case 2: // const size, no per-glyph offsets
                return (ImageDataOffset + (uint)localIdx * ConstImageSize, ConstImageSize);
            default:
                return (0, 0);
        }
    }
}

/// <summary>
/// Parsed 'CBLC' (Color Bitmap Location) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/cblc
/// </summary>
public sealed class CblcTable
{
    public BitmapStrike[] Strikes { get; }

    public CblcTable(BitmapStrike[] strikes) => Strikes = strikes;

    /// <summary>
    /// Pick the strike whose PPEM is the smallest one >= the requested size,
    /// falling back to the largest available if all are smaller.
    /// </summary>
    public BitmapStrike? PickStrike(float pixelsPerEm)
    {
        if (Strikes.Length == 0) return null;
        BitmapStrike? bestUp = null;
        BitmapStrike? bestDown = null;
        foreach (var s in Strikes)
        {
            if (s.PpemY >= pixelsPerEm)
            {
                if (bestUp is null || s.PpemY < bestUp.PpemY) bestUp = s;
            }
            else
            {
                if (bestDown is null || s.PpemY > bestDown.PpemY) bestDown = s;
            }
        }
        return bestUp ?? bestDown;
    }

    public static CblcTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // majorVersion(uint16) + minorVersion(uint16)
        r.Skip(4);
        var numSizes = r.ReadUInt32();

        var strikes = new BitmapStrike[numSizes];
        for (var i = 0; i < numSizes; i++)
            strikes[i] = ParseStrike(data, ref r);
        return new CblcTable(strikes);
    }

    private static BitmapStrike ParseStrike(ReadOnlySpan<byte> tableData, ref BigEndianReader r)
    {
        var indexSubArrayOffset = r.ReadUInt32();
        var indexTablesSize = r.ReadUInt32();
        var numberOfIndexSubTables = r.ReadUInt32();
        var colorRef = r.ReadUInt32(); _ = colorRef;
        // hori(SbitLineMetrics) + vert(SbitLineMetrics) — 12+12 bytes
        r.Skip(24);
        var startGlyphIndex = r.ReadUInt16();
        var endGlyphIndex = r.ReadUInt16();
        var ppemX = r.ReadByte();
        var ppemY = r.ReadByte();
        var bitDepth = r.ReadByte(); _ = bitDepth;
        var flags = r.ReadSByte(); _ = flags;
        _ = indexTablesSize;

        // IndexSubTableArray @ indexSubArrayOffset
        var subtables = new IndexSubtable[numberOfIndexSubTables];
        var arr = new BigEndianReader(tableData, (int)indexSubArrayOffset);
        for (var k = 0; k < numberOfIndexSubTables; k++)
        {
            var firstGid = arr.ReadUInt16();
            var lastGid = arr.ReadUInt16();
            var addOff = arr.ReadUInt32();
            // Subtable starts at indexSubArrayOffset + addOff.
            subtables[k] = ParseIndexSubtable(tableData, (int)(indexSubArrayOffset + addOff),
                firstGid, lastGid);
        }

        return new BitmapStrike(ppemX, ppemY, startGlyphIndex, endGlyphIndex, subtables);
    }

    private static IndexSubtable ParseIndexSubtable(ReadOnlySpan<byte> tableData, int offset,
        ushort firstGid, ushort lastGid)
    {
        var r = new BigEndianReader(tableData, offset);
        // IndexSubHeader: indexFormat(uint16) + imageFormat(uint16) + imageDataOffset(uint32)
        var indexFormat = r.ReadUInt16();
        var imageFormat = r.ReadUInt16();
        var imageDataOffset = r.ReadUInt32();
        var n = lastGid - firstGid + 1;

        switch (indexFormat)
        {
            case 1:
            {
                // sbitOffsets[n + 1] — uint32 each
                var off = new uint[n + 1];
                for (var i = 0; i <= n; i++) off[i] = r.ReadUInt32();
                return new IndexSubtable(firstGid, lastGid, indexFormat,
                    (BitmapImageFormat)imageFormat, imageDataOffset, off, 0, null);
            }
            case 2:
            {
                var imageSize = r.ReadUInt32();
                // bigMetrics — 8 bytes
                var metrics = r.ReadBytes(8).ToArray();
                return new IndexSubtable(firstGid, lastGid, indexFormat,
                    (BitmapImageFormat)imageFormat, imageDataOffset, null, imageSize, metrics);
            }
            case 3:
            {
                // sbitOffsets[n + 1] — uint16 each
                var off = new uint[n + 1];
                for (var i = 0; i <= n; i++) off[i] = r.ReadUInt16();
                return new IndexSubtable(firstGid, lastGid, indexFormat,
                    (BitmapImageFormat)imageFormat, imageDataOffset, off, 0, null);
            }
            default:
                // Format 4/5 (sparse) — return empty subtable (covers no glyphs).
                return new IndexSubtable(firstGid, lastGid, indexFormat,
                    (BitmapImageFormat)imageFormat, imageDataOffset, null, 0, null);
        }
    }
}
