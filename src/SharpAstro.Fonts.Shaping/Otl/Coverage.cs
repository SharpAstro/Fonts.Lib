using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// OpenType Coverage table: the set of glyphs a lookup subtable applies to, with
/// each covered glyph's ordinal index into the subtable's parallel data arrays.
/// Format 1 is an explicit sorted glyph list; format 2 is sorted ranges carrying a
/// running start index. Ranges are kept as ranges (not flattened to a glyph array
/// like the core's kerning-only GPOS slice does) — CJK fonts cover tens of
/// thousands of glyphs and shaping holds many coverages alive at once.
/// </summary>
internal sealed class Coverage
{
    // Format 1: sorted covered glyph ids; coverage index == array index.
    private readonly ushort[]? _glyphs;

    // Format 2: sorted, non-overlapping ranges; coverage index == startCoverageIndex + (gid - start).
    private readonly (ushort Start, ushort End, ushort StartCoverageIndex)[]? _ranges;

    private Coverage(ushort[] glyphs) => _glyphs = glyphs;
    private Coverage((ushort, ushort, ushort)[] ranges) => _ranges = ranges;

    /// <summary>Empty coverage — no glyph matches. Used for unparseable data (never null-refs a lookup).</summary>
    public static readonly Coverage Empty = new(Array.Empty<ushort>());

    /// <summary>Coverage index for <paramref name="glyphId"/>, or −1 when not covered.</summary>
    public int GetCoverageIndex(uint glyphId)
    {
        if (_glyphs is not null)
        {
            int lo = 0, hi = _glyphs.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var g = _glyphs[mid];
                if (glyphId < g) hi = mid - 1;
                else if (glyphId > g) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        var ranges = _ranges!;
        {
            int lo = 0, hi = ranges.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var r = ranges[mid];
                if (glyphId < r.Start) hi = mid - 1;
                else if (glyphId > r.End) lo = mid + 1;
                else return r.StartCoverageIndex + (int)(glyphId - r.Start);
            }
            return -1;
        }
    }

    public bool Contains(uint glyphId) => GetCoverageIndex(glyphId) >= 0;

    /// <summary>
    /// Parse a coverage table at <paramref name="offset"/> within <paramref name="table"/>
    /// (offset relative to the containing subtable, per spec). Malformed data yields
    /// <see cref="Empty"/> — a broken font degrades to "lookup never matches", never a throw.
    /// </summary>
    public static Coverage Parse(ReadOnlySpan<byte> table, int offset)
    {
        if (offset <= 0 || offset + 4 > table.Length) return Empty;
        var r = new BigEndianReader(table[offset..]);
        var format = r.ReadUInt16();

        if (format == 1)
        {
            var count = r.ReadUInt16();
            if (r.Remaining < count * 2) return Empty;
            var glyphs = new ushort[count];
            for (var i = 0; i < count; i++) glyphs[i] = r.ReadUInt16();
            return new Coverage(glyphs);
        }

        if (format == 2)
        {
            var rangeCount = r.ReadUInt16();
            if (r.Remaining < rangeCount * 6) return Empty;
            var ranges = new (ushort, ushort, ushort)[rangeCount];
            for (var i = 0; i < rangeCount; i++)
                ranges[i] = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
            return new Coverage(ranges);
        }

        return Empty;
    }
}
