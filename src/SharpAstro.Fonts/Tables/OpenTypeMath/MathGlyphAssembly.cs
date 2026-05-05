using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// Assembly recipe for a stretchable glyph: how to build an arbitrary-height
/// (or arbitrary-width) instance by stacking <see cref="MathGlyphPart"/>s.
/// Used when no pre-drawn variant in <see cref="MathGlyphConstruction.Variants"/>
/// is large enough — the consumer extends the assembly by repeating extender
/// parts until the required size is reached.
/// </summary>
public sealed class MathGlyphAssembly
{
    /// <summary>Italics correction, in FUnits — shift to apply to glyphs
    /// following an assembled italic shape so the next glyph clears the slant.
    /// 0 for upright shapes (radicals, brackets, the common cases).</summary>
    public short ItalicsCorrection { get; }

    /// <summary>Parts of the assembly, in stretch-axis order (bottom-up for
    /// vertical, left-to-right for horizontal). At least one extender (where
    /// <see cref="MathGlyphPart.IsExtender"/> is true) is required for the
    /// assembly to be growable; pure fixed-piece assemblies are unusual.</summary>
    public IReadOnlyList<MathGlyphPart> Parts { get; }

    private MathGlyphAssembly(short italicsCorrection, MathGlyphPart[] parts)
    {
        ItalicsCorrection = italicsCorrection;
        Parts = parts;
    }

    /// <summary>
    /// Parse a GlyphAssembly subtable. <paramref name="data"/> starts at the
    /// subtable's own offset (NOT at the parent table). The italics correction
    /// occupies a MathValueRecord; we keep the value and discard the device-
    /// table offset (we don't pixel-snap).
    /// </summary>
    internal static MathGlyphAssembly Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var italicsCorrection = r.ReadInt16();
        r.Skip(2); // device table offset — ignored
        var partCount = r.ReadUInt16();
        var parts = new MathGlyphPart[partCount];
        for (var i = 0; i < partCount; i++)
        {
            var glyphId = r.ReadUInt16();
            var startConn = r.ReadUInt16();
            var endConn = r.ReadUInt16();
            var fullAdv = r.ReadUInt16();
            // partFlags: bit 0 = extender, other bits reserved.
            var partFlags = r.ReadUInt16();
            parts[i] = new MathGlyphPart(glyphId, startConn, endConn, fullAdv, (partFlags & 0x1) != 0);
        }
        return new MathGlyphAssembly(italicsCorrection, parts);
    }
}
