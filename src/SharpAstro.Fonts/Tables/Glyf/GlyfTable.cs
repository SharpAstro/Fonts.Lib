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
        return DecodeGlyph(span, null);
    }

    /// <summary>
    /// Decode an outline applying per-component offset variation deltas when
    /// <paramref name="componentOffsetDeltas"/> is non-null. Only used for
    /// composite glyphs where gvar provides component-anchor adjustments.
    /// Each element is (dx, dy) to add to the corresponding component's
    /// arg1/arg2 translation values before assembling the composite outline.
    /// </summary>
    public Outline LoadGlyphWithVariation(uint glyphId,
        (float Dx, float Dy)[]? componentOffsetDeltas)
    {
        var len = _loca.GetLength(glyphId);
        if (len == 0) return Outline.Empty;

        var span = _data.Span.Slice((int)_loca.GetOffset(glyphId), (int)len);
        return DecodeGlyph(span, componentOffsetDeltas);
    }

    /// <summary>
    /// Returns true if the glyph at <paramref name="glyphId"/> is a composite
    /// glyph (numContours == -1). Returns false for empty / simple glyphs.
    /// </summary>
    public bool IsComposite(uint glyphId)
    {
        var len = _loca.GetLength(glyphId);
        if (len < 2) return false;
        var span = _data.Span.Slice((int)_loca.GetOffset(glyphId), (int)len);
        // numContours is the first int16; negative = composite.
        return (short)((span[0] << 8) | span[1]) < 0;
    }

    /// <summary>
    /// Count the number of components in a composite glyph. Used to
    /// determine how many gvar delta entries to read for component-offset
    /// variation. Returns 0 for non-composite or empty glyphs.
    /// </summary>
    public int GetComponentCount(uint glyphId)
    {
        var len = _loca.GetLength(glyphId);
        if (len < 10) return 0;  // header is 10 bytes min
        var span = _data.Span.Slice((int)_loca.GetOffset(glyphId), (int)len);
        var r = new BigEndianReader(span);
        if (r.ReadInt16() >= 0) return 0;  // not composite
        r.Skip(8);  // skip bbox

        // Composite flag constants (duplicated from CompositeGlyphParser for self-containment).
        const ushort ArgsAreWords       = 0x0001;
        const ushort WeHaveAScale       = 0x0008;
        const ushort MoreComponents     = 0x0020;
        const ushort WeHaveAnXAndYScale = 0x0040;
        const ushort WeHaveATwoByTwo    = 0x0080;

        var count = 0;
        ushort flag;
        do
        {
            if (r.Remaining < 4) break;
            flag = r.ReadUInt16();
            r.Skip(2); // glyphIndex
            // Skip arg1+arg2
            r.Skip((flag & ArgsAreWords) != 0 ? 4 : 2);
            // Skip transform
            if ((flag & WeHaveAScale) != 0) r.Skip(2);
            else if ((flag & WeHaveAnXAndYScale) != 0) r.Skip(4);
            else if ((flag & WeHaveATwoByTwo) != 0) r.Skip(8);
            count++;
        } while ((flag & MoreComponents) != 0 && r.Remaining > 0);

        return count;
    }

    private Outline DecodeGlyph(ReadOnlySpan<byte> span,
        (float Dx, float Dy)[]? componentOffsetDeltas)
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

        // Composite glyph: -1. Pass component offset deltas for gvar variation.
        return CompositeGlyphParser.Parse(ref r, bounds, LoadGlyph, componentOffsetDeltas);
    }
}
