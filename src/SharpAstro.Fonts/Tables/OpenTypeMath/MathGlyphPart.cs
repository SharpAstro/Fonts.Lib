namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// One piece of a stretchable-glyph assembly: a fixed end-cap (top hook,
/// bottom hook, side bracket etc.) or an extender that's repeated as many
/// times as needed to fill the gap. Parts are listed bottom-up for vertical
/// assemblies and left-to-right for horizontal.
///
/// <para>The connector lengths describe how much of this part overlaps with
/// the next/previous part — the actual piece is rendered in full but
/// overlapped by <c>min(thisEnd, nextStart)</c> with its neighbour, with at
/// least <see cref="MathTable.MinConnectorOverlap"/> required for visual
/// continuity.</para>
/// </summary>
/// <param name="GlyphId">Glyph id of this assembly piece.</param>
/// <param name="StartConnectorLength">Maximum connection overlap on the
/// "previous neighbour" side, in FUnits.</param>
/// <param name="EndConnectorLength">Maximum overlap on the "next neighbour"
/// side, in FUnits.</param>
/// <param name="FullAdvance">Full size of the piece along the stretch axis,
/// in FUnits (the size before any overlap is subtracted).</param>
/// <param name="IsExtender">True if this piece may be repeated to grow the
/// assembly. False for fixed end-caps.</param>
public readonly record struct MathGlyphPart(
    ushort GlyphId,
    ushort StartConnectorLength,
    ushort EndConnectorLength,
    ushort FullAdvance,
    bool IsExtender);
