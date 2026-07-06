using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// OpenType ClassDef table: glyph id → integer class. Unlisted glyphs are class 0.
/// Format 1 is a sequential array from a start glyph; format 2 is sorted class ranges.
/// Used by GDEF (glyph classes, mark-attachment classes) and class-based lookup
/// subtables (pair kerning, contextual class matching).
/// </summary>
internal sealed class ClassDef
{
    // Format 1: sequential classes for glyphs starting at _startGlyph.
    private readonly ushort _startGlyph;
    private readonly ushort[]? _classes;

    // Format 2: sorted class ranges.
    private readonly (ushort Start, ushort End, ushort Class)[]? _ranges;

    private ClassDef(ushort startGlyph, ushort[] classes)
    {
        _startGlyph = startGlyph;
        _classes = classes;
    }

    private ClassDef((ushort, ushort, ushort)[] ranges) => _ranges = ranges;

    /// <summary>Empty ClassDef — every glyph is class 0.</summary>
    public static readonly ClassDef Empty = new(Array.Empty<(ushort, ushort, ushort)>());

    public int GetClass(uint glyphId)
    {
        if (_classes is not null)
        {
            var idx = (int)glyphId - _startGlyph;
            return (uint)idx < (uint)_classes.Length ? _classes[idx] : 0;
        }

        var ranges = _ranges!;
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = ranges[mid];
            if (glyphId < r.Start) hi = mid - 1;
            else if (glyphId > r.End) lo = mid + 1;
            else return r.Class;
        }
        return 0;
    }

    // ---- Span-direct probe (zero-alloc hot path) --------------------------------------
    // Class-based GPOS pair kerning and contextual class matching probe a ClassDef per
    // application attempt; span-direct avoids materializing a ClassDef + array each time. Parse +
    // the instance GetClass are retained for GDEF's glyph/mark-attach classes (parsed once,
    // queried per glyph, where a materialized array indexes faster than re-reading bytes).

    /// <summary>Class of <paramref name="glyphId"/> in the ClassDef at <paramref name="offset"/>
    /// within <paramref name="table"/> (0 = unlisted / malformed). Zero-allocation.</summary>
    public static int ClassOf(ReadOnlySpan<byte> table, int offset, uint glyphId)
    {
        if (offset <= 0 || offset + 4 > table.Length) return 0;
        var format = ReadU16(table, offset);
        if (format == 1)
        {
            var startGlyph = ReadU16(table, offset + 2);
            var count = ReadU16(table, offset + 4);
            var arr = offset + 6;
            if (arr + count * 2 > table.Length) return 0;
            var idx = (long)glyphId - startGlyph;
            return (ulong)idx < (ulong)count ? ReadU16(table, arr + (int)idx * 2) : 0;
        }
        if (format == 2)
        {
            var rangeCount = ReadU16(table, offset + 2);
            var arr = offset + 4;
            if (arr + rangeCount * 6 > table.Length) return 0;
            int lo = 0, hi = rangeCount - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var rec = arr + mid * 6;               // range: start(2), end(2), class(2)
                var start = ReadU16(table, rec);
                if (glyphId < start) { hi = mid - 1; continue; }
                var end = ReadU16(table, rec + 2);
                if (glyphId > end) { lo = mid + 1; continue; }
                return ReadU16(table, rec + 4);
            }
            return 0;
        }
        return 0;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int offset)
        => (ushort)((b[offset] << 8) | b[offset + 1]);

    /// <summary>
    /// Parse a ClassDef at <paramref name="offset"/> within <paramref name="table"/>
    /// (subtable-relative offset, per spec). Malformed data yields <see cref="Empty"/>.
    /// </summary>
    public static ClassDef Parse(ReadOnlySpan<byte> table, int offset)
    {
        if (offset <= 0 || offset + 4 > table.Length) return Empty;
        var r = new BigEndianReader(table[offset..]);
        var format = r.ReadUInt16();

        if (format == 1)
        {
            if (r.Remaining < 4) return Empty;
            var startGlyph = r.ReadUInt16();
            var count = r.ReadUInt16();
            if (r.Remaining < count * 2) return Empty;
            var classes = new ushort[count];
            for (var i = 0; i < count; i++) classes[i] = r.ReadUInt16();
            return new ClassDef(startGlyph, classes);
        }

        if (format == 2)
        {
            var rangeCount = r.ReadUInt16();
            if (r.Remaining < rangeCount * 6) return Empty;
            var ranges = new (ushort, ushort, ushort)[rangeCount];
            for (var i = 0; i < rangeCount; i++)
                ranges[i] = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
            return new ClassDef(ranges);
        }

        return Empty;
    }
}
