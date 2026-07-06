using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// An OpenType Anchor table: a single (x, y) attachment point in font design units,
/// used by mark positioning (GPOS 4/5/6) and cursive attachment (GPOS 3). All three
/// formats carry x/y at the same offset; the extra data differs and is deliberately
/// dropped:
/// <list type="bullet">
/// <item>Format 1 — plain (x, y).</item>
/// <item>Format 2 — adds a contour <c>anchorPoint</c> index for TrueType-hinted
/// attachment; irrelevant to SDF/scalable rendering, so we take the design (x, y).</item>
/// <item>Format 3 — adds Device/VariationIndex tables (hinting-grid deltas /
/// variation deltas); both are plan non-goals, so again the design (x, y).</item>
/// </list>
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/gpos#anchor-tables</para>
/// </summary>
internal static class Anchor
{
    /// <summary>
    /// Read the anchor at <paramref name="offset"/> within <paramref name="table"/>
    /// (offset relative to the containing MarkArray/BaseArray/etc., per spec). A zero or
    /// out-of-range offset means "no anchor" — the caller treats that as no attachment,
    /// exactly as a NULL offset does in HarfBuzz.
    /// </summary>
    public static bool TryGet(ReadOnlySpan<byte> table, int offset, out short x, out short y)
    {
        x = 0;
        y = 0;
        // format(2) + x(2) + y(2) = 6 bytes minimum for every format.
        if (offset <= 0 || offset + 6 > table.Length) return false;
        var r = new BigEndianReader(table[offset..]);
        var format = r.ReadUInt16();
        if (format is < 1 or > 3) return false;
        x = r.ReadInt16();
        y = r.ReadInt16();
        return true;
    }
}
