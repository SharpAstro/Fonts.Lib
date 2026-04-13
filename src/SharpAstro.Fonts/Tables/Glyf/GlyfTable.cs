using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Tables.Loca;

namespace SharpAstro.Fonts.Tables.Glyf;

/// <summary>
/// Lazy view over the 'glyf' table. Holds only the raw byte slice + a
/// reference to the parsed 'loca' offsets — outlines are decoded on demand
/// in <see cref="LoadGlyph"/>.
///
/// Thread-safe: all state is immutable; parsing allocates per-call.
/// </summary>
public sealed class GlyfTable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly LocaTable _loca;

    public GlyfTable(ReadOnlyMemory<byte> data, LocaTable loca)
    {
        _data = data;
        _loca = loca;
    }

    /// <summary>
    /// Decode an outline. Returns <see cref="Outline.Empty"/> for glyphs with
    /// zero length in 'loca' (e.g. space).
    /// </summary>
    public Outline LoadGlyph(uint glyphId)
    {
        var len = _loca.GetLength(glyphId);
        if (len == 0) return Outline.Empty;

        var span = _data.Span.Slice((int)_loca.GetOffset(glyphId), (int)len);
        return DecodeGlyph(span);
    }

    private Outline DecodeGlyph(ReadOnlySpan<byte> span)
    {
        var r = new BigEndianReader(span);
        var numContours = r.ReadInt16();
        var xMin = r.ReadInt16();
        var yMin = r.ReadInt16();
        var xMax = r.ReadInt16();
        var yMax = r.ReadInt16();
        var bounds = (xMin, yMin, xMax, yMax);

        if (numContours >= 0)
            return SimpleGlyphParser.Parse(ref r, numContours, bounds);

        // Composite glyph: -1.
        return CompositeGlyphParser.Parse(ref r, bounds, LoadGlyph);
    }
}
