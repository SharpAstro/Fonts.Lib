using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// OpenType MATH <c>MathGlyphInfo</c> subtable — per-glyph metric extras
/// the global <see cref="MathConstants"/> can't express:
/// <list type="bullet">
/// <item><b>Italics correction</b> (<see cref="GetItalicsCorrection"/>) —
/// extra space to the right of an italic base when no following script
/// pulls under it. Used to keep an upright '+' from collapsing into the
/// slant of an italic 'f'.</item>
/// <item><b>Top-accent attachment</b> (<see cref="GetTopAccentAttachment"/>) —
/// the x-coordinate, measured from the glyph's left side bearing, where a
/// top accent (macron, hat, tilde, dot, …) should anchor. Without this,
/// accents over slanted glyphs land off-centre.</item>
/// <item><b>Extended-shape coverage</b> (<see cref="IsExtendedShape"/>) —
/// glyphs treated as "tall" for accent placement: their accents sit above
/// the actual ascent rather than at the constant <see cref="MathConstants.AccentBaseHeight"/>.
/// Big integral, big sigma, big radical, etc.</item>
/// <item><b>Math kern info</b> (<see cref="GetKernInfo"/>) — per-corner
/// "cut-in" kerns, four <see cref="MathKern"/> step functions per glyph,
/// for tucking sub/superscripts into the slopes of slanted bases.</item>
/// </list>
///
/// <para>Each of the four sub-subtables is independently optional; a font
/// may supply any subset. Lookups for absent data return the documented
/// "no value" answer (zero italics correction, null top-accent attachment,
/// false for extended-shape, null kern record).</para>
///
/// <para>Spec: <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/math#mathglyphinfo-table"/></para>
/// </summary>
public sealed class MathGlyphInfo
{
    private readonly Dictionary<ushort, short> _italicsCorrection;
    private readonly Dictionary<ushort, short> _topAccentAttachment;
    private readonly HashSet<ushort> _extendedShape;
    private readonly Dictionary<ushort, MathKernInfoRecord> _kernInfo;

    private MathGlyphInfo(
        Dictionary<ushort, short> italicsCorrection,
        Dictionary<ushort, short> topAccentAttachment,
        HashSet<ushort> extendedShape,
        Dictionary<ushort, MathKernInfoRecord> kernInfo)
    {
        _italicsCorrection = italicsCorrection;
        _topAccentAttachment = topAccentAttachment;
        _extendedShape = extendedShape;
        _kernInfo = kernInfo;
    }

    /// <summary>
    /// Italics correction (FUnits) for <paramref name="glyphId"/> — the
    /// horizontal padding to add at the right edge of an italic glyph
    /// before the next non-script atom. Returns 0 when the glyph isn't in
    /// coverage; treat that as "no correction needed", which is correct
    /// for upright glyphs and for italic glyphs the font doesn't bother
    /// flagging.
    /// </summary>
    public short GetItalicsCorrection(ushort glyphId)
        => _italicsCorrection.TryGetValue(glyphId, out var v) ? v : (short)0;

    /// <summary>
    /// Top-accent attachment x-coordinate (FUnits, from glyph origin/LSB)
    /// for <paramref name="glyphId"/>. Returns null when the glyph has no
    /// entry — callers should fall back to <c>advance / 2</c>, the default
    /// the spec prescribes for "centre over the advance width".
    /// </summary>
    public short? GetTopAccentAttachment(ushort glyphId)
        => _topAccentAttachment.TryGetValue(glyphId, out var v) ? v : null;

    /// <summary>
    /// True if <paramref name="glyphId"/> is on the extended-shape coverage
    /// list. Affects accent placement: an extended-shape base's accent
    /// sits above the glyph's actual top, not at the font's
    /// <see cref="MathConstants.AccentBaseHeight"/>.
    /// </summary>
    public bool IsExtendedShape(ushort glyphId) => _extendedShape.Contains(glyphId);

    /// <summary>
    /// Per-corner cut-in kerns for <paramref name="glyphId"/>. Returns
    /// null when the glyph has no kerning entry — most glyphs don't.
    /// </summary>
    public MathKernInfoRecord? GetKernInfo(ushort glyphId)
        => _kernInfo.TryGetValue(glyphId, out var v) ? v : null;

    /// <summary>
    /// Parse a <c>MathGlyphInfo</c> subtable. <paramref name="data"/> is
    /// the slice starting at the subtable's own offset within the parent
    /// MATH table.
    /// </summary>
    internal static MathGlyphInfo Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var italicsOffset = r.ReadUInt16();
        var topAccentOffset = r.ReadUInt16();
        var extendedShapeOffset = r.ReadUInt16();
        var kernInfoOffset = r.ReadUInt16();

        var italics = new Dictionary<ushort, short>();
        var topAccent = new Dictionary<ushort, short>();
        var extended = new HashSet<ushort>();
        var kernInfo = new Dictionary<ushort, MathKernInfoRecord>();

        if (italicsOffset != 0 && italicsOffset < data.Length)
            ParseValueIndexedTable(data[italicsOffset..], italics);

        if (topAccentOffset != 0 && topAccentOffset < data.Length)
            ParseValueIndexedTable(data[topAccentOffset..], topAccent);

        if (extendedShapeOffset != 0 && extendedShapeOffset < data.Length)
        {
            // Extended shape is a bare Coverage table — just the glyph list.
            foreach (var g in MathTable.ParseCoverageInternal(data[extendedShapeOffset..]))
                extended.Add(g);
        }

        if (kernInfoOffset != 0 && kernInfoOffset < data.Length)
            ParseKernInfo(data[kernInfoOffset..], kernInfo);

        return new MathGlyphInfo(italics, topAccent, extended, kernInfo);
    }

    /// <summary>
    /// Parse the layout shared by both <c>MathItalicsCorrectionInfo</c> and
    /// <c>MathTopAccentAttachment</c>: a Coverage table followed by a
    /// parallel array of <c>MathValueRecord</c>s. The MathValueRecord's
    /// device-table offset is discarded (we don't pixel-snap).
    /// </summary>
    private static void ParseValueIndexedTable(
        ReadOnlySpan<byte> data,
        Dictionary<ushort, short> map)
    {
        var r = new BigEndianReader(data);
        var coverageOffset = r.ReadUInt16();
        var count = r.ReadUInt16();
        var values = new short[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = r.ReadInt16();
            r.Skip(2); // device table offset
        }
        if (coverageOffset == 0 || coverageOffset >= data.Length) return;

        var glyphs = MathTable.ParseCoverageInternal(data[coverageOffset..]);
        // Per spec the coverage length must equal the value count, but
        // clamp defensively rather than throw — same posture as the
        // existing variants parser.
        var pairCount = System.Math.Min(glyphs.Length, values.Length);
        for (var i = 0; i < pairCount; i++) map[glyphs[i]] = values[i];
    }

    /// <summary>
    /// Parse a <c>MathKernInfo</c> subtable: coverage + parallel
    /// <c>MathKernInfoRecord</c>s, each pointing to up to four
    /// <c>MathKern</c> subtables.
    /// </summary>
    private static void ParseKernInfo(
        ReadOnlySpan<byte> data,
        Dictionary<ushort, MathKernInfoRecord> map)
    {
        var r = new BigEndianReader(data);
        var coverageOffset = r.ReadUInt16();
        var count = r.ReadUInt16();

        // Each MathKernInfoRecord is exactly 4 Offset16s (8 bytes), all
        // relative to MathKernInfo's own start.
        var topRight = new ushort[count];
        var topLeft = new ushort[count];
        var bottomRight = new ushort[count];
        var bottomLeft = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            topRight[i] = r.ReadUInt16();
            topLeft[i] = r.ReadUInt16();
            bottomRight[i] = r.ReadUInt16();
            bottomLeft[i] = r.ReadUInt16();
        }
        if (coverageOffset == 0 || coverageOffset >= data.Length) return;

        var glyphs = MathTable.ParseCoverageInternal(data[coverageOffset..]);
        var pairCount = System.Math.Min(glyphs.Length, count);
        for (var i = 0; i < pairCount; i++)
        {
            map[glyphs[i]] = new MathKernInfoRecord(
                ResolveKern(data, topRight[i]),
                ResolveKern(data, topLeft[i]),
                ResolveKern(data, bottomRight[i]),
                ResolveKern(data, bottomLeft[i]));
        }
    }

    private static MathKern? ResolveKern(ReadOnlySpan<byte> kernInfoBase, ushort offset)
    {
        if (offset == 0 || offset >= kernInfoBase.Length) return null;
        return MathKern.Parse(kernInfoBase[offset..]);
    }
}
