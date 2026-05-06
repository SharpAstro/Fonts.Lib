using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// OpenType MATH table. Provides global math typesetting constants
/// (axis height, fraction/radical rule thickness, sub/super shifts —
/// see <see cref="Constants"/>) plus per-glyph stretch recipes for
/// scalable radicals, brackets, big operators, and similar shapes
/// (<see cref="GetVerticalConstruction"/> / <see cref="GetHorizontalConstruction"/>).
///
/// <para>Math fonts that ship with this table: STIX Two Math, Latin Modern
/// Math, Cambria Math, Asana Math, Libertinus Math, Fira Math, the TeX-Gyre
/// math family. General-purpose UI fonts (DejaVu, Roboto, Source Sans) do
/// not — for those, <see cref="OpenTypeFont.Math"/> is null.</para>
///
/// <para>Spec: <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/math"/></para>
/// </summary>
public sealed class MathTable
{
    public MathConstants Constants { get; }

    /// <summary>Per-glyph metric extras (italics correction, top-accent
    /// attachment, extended-shape coverage, corner kerning). Null when
    /// the font's MATH table omits the <c>MathGlyphInfo</c> subtable —
    /// most general-purpose fonts ship constants and variants only.</summary>
    public MathGlyphInfo? GlyphInfo { get; }

    /// <summary>Minimum required overlap (FUnits) between adjacent assembly
    /// pieces when stacking <see cref="MathGlyphPart"/>s. Applies to both
    /// vertical and horizontal assemblies.</summary>
    public ushort MinConnectorOverlap { get; }

    private readonly Dictionary<ushort, MathGlyphConstruction> _vertical;
    private readonly Dictionary<ushort, MathGlyphConstruction> _horizontal;

    private MathTable(
        MathConstants constants,
        MathGlyphInfo? glyphInfo,
        ushort minConnectorOverlap,
        Dictionary<ushort, MathGlyphConstruction> vertical,
        Dictionary<ushort, MathGlyphConstruction> horizontal)
    {
        Constants = constants;
        GlyphInfo = glyphInfo;
        MinConnectorOverlap = minConnectorOverlap;
        _vertical = vertical;
        _horizontal = horizontal;
    }

    /// <summary>
    /// Vertical stretching recipe for <paramref name="glyphId"/> — used for
    /// radicals (the √ glyph), brackets/parens/braces, big operators with
    /// limits stacked above/below. Returns null if this glyph has no entry
    /// in the vertical coverage.
    /// </summary>
    public MathGlyphConstruction? GetVerticalConstruction(ushort glyphId)
        => _vertical.TryGetValue(glyphId, out var c) ? c : null;

    /// <summary>
    /// Horizontal stretching recipe for <paramref name="glyphId"/> — used for
    /// over/underbraces, arrows, wide accents. Returns null if this glyph has
    /// no entry in the horizontal coverage.
    /// </summary>
    public MathGlyphConstruction? GetHorizontalConstruction(ushort glyphId)
        => _horizontal.TryGetValue(glyphId, out var c) ? c : null;

    /// <summary>
    /// Parse a MATH table. <paramref name="data"/> is the slice starting at
    /// the table's own offset within the font file.
    /// </summary>
    public static MathTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var majorVersion = r.ReadUInt16();
        var minorVersion = r.ReadUInt16();
        if (majorVersion != 1)
            throw new InvalidDataException(
                $"Unsupported MATH table version {majorVersion}.{minorVersion} (expected 1.x).");

        var constantsOffset = r.ReadUInt16();
        var glyphInfoOffset = r.ReadUInt16();
        var variantsOffset = r.ReadUInt16();

        if (constantsOffset == 0 || constantsOffset >= data.Length)
            throw new InvalidDataException("MATH table is missing the MathConstants subtable.");

        var constants = MathConstants.Parse(data[constantsOffset..]);

        MathGlyphInfo? glyphInfo = null;
        if (glyphInfoOffset != 0 && glyphInfoOffset < data.Length)
            glyphInfo = MathGlyphInfo.Parse(data[glyphInfoOffset..]);

        var vertical = new Dictionary<ushort, MathGlyphConstruction>();
        var horizontal = new Dictionary<ushort, MathGlyphConstruction>();
        ushort minOverlap = 0;

        if (variantsOffset != 0 && variantsOffset < data.Length)
        {
            ParseVariants(data[variantsOffset..], vertical, horizontal, out minOverlap);
        }

        return new MathTable(constants, glyphInfo, minOverlap, vertical, horizontal);
    }

    private static void ParseVariants(
        ReadOnlySpan<byte> data,
        Dictionary<ushort, MathGlyphConstruction> vertical,
        Dictionary<ushort, MathGlyphConstruction> horizontal,
        out ushort minConnectorOverlap)
    {
        var r = new BigEndianReader(data);
        minConnectorOverlap = r.ReadUInt16();
        var vertCoverageOffset = r.ReadUInt16();
        var horizCoverageOffset = r.ReadUInt16();
        var vertGlyphCount = r.ReadUInt16();
        var horizGlyphCount = r.ReadUInt16();

        // Read both arrays of construction offsets in order, then pair with
        // their coverage tables. Offsets are relative to MathVariants start.
        var vertOffsets = new ushort[vertGlyphCount];
        for (var i = 0; i < vertGlyphCount; i++) vertOffsets[i] = r.ReadUInt16();
        var horizOffsets = new ushort[horizGlyphCount];
        for (var i = 0; i < horizGlyphCount; i++) horizOffsets[i] = r.ReadUInt16();

        FillConstructionMap(data, vertCoverageOffset, vertOffsets, vertical);
        FillConstructionMap(data, horizCoverageOffset, horizOffsets, horizontal);
    }

    private static void FillConstructionMap(
        ReadOnlySpan<byte> variantsData,
        ushort coverageOffset,
        ushort[] constructionOffsets,
        Dictionary<ushort, MathGlyphConstruction> map)
    {
        if (coverageOffset == 0 || coverageOffset >= variantsData.Length)
            return;

        var glyphIds = ParseCoverageInternal(variantsData[coverageOffset..]);
        // Per spec the coverage length must equal the glyph count, but real
        // fonts have been seen to disagree in pathological cases — clamp
        // defensively rather than throw, so a buggy font doesn't take the
        // whole table down.
        var pairCount = System.Math.Min(glyphIds.Length, constructionOffsets.Length);
        for (var i = 0; i < pairCount; i++)
        {
            var off = constructionOffsets[i];
            if (off == 0 || off >= variantsData.Length) continue;
            map[glyphIds[i]] = MathGlyphConstruction.Parse(variantsData[off..]);
        }
    }

    /// <summary>
    /// Parse a Coverage table (Format 1 = explicit list, Format 2 = ranges).
    /// Returned glyph ids are in coverage order — the same order the parent
    /// table uses to index into its parallel construction array. Internal
    /// so the sibling <see cref="MathGlyphInfo"/> parser in this same
    /// namespace can reuse it without a cross-cutting refactor.
    /// </summary>
    internal static ushort[] ParseCoverageInternal(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var format = r.ReadUInt16();
        if (format == 1)
        {
            var count = r.ReadUInt16();
            var glyphs = new ushort[count];
            for (var i = 0; i < count; i++) glyphs[i] = r.ReadUInt16();
            return glyphs;
        }
        if (format == 2)
        {
            var rangeCount = r.ReadUInt16();
            // Two-pass: count then fill, so we allocate exactly. Per-spec the
            // ranges are non-overlapping and sorted, so total = sum(end-start+1).
            var startPos = r.Position;
            var total = 0;
            for (var i = 0; i < rangeCount; i++)
            {
                var start = r.ReadUInt16();
                var end = r.ReadUInt16();
                r.Skip(2); // startCoverageIndex — ignored; we rebuild order from the loop
                total += end - start + 1;
            }
            var glyphs = new ushort[total];
            r.Position = startPos;
            var idx = 0;
            for (var i = 0; i < rangeCount; i++)
            {
                var start = r.ReadUInt16();
                var end = r.ReadUInt16();
                r.Skip(2);
                for (var g = start; g <= end; g++) glyphs[idx++] = (ushort)g;
            }
            return glyphs;
        }
        return [];
    }
}
