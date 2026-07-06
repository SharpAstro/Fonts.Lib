using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The shaping entry point: dispatches a run to the shaper for its script and returns the
/// shaped <see cref="ShapeBuffer"/> (glyph ids, clusters, and position deltas in font units).
///
/// <para><b>H4 status:</b> the full non-variable lookup set is applied — GSUB 1 (single), 2
/// (multiple), 3 (alternate), 4 (ligature), 5/6 (context/chained context), 8 (reverse chaining)
/// and GPOS 1 (single), 2 (pair), 3 (cursive), 4/5/6 (mark attachment), 7/8 (context/chained
/// context). Two shapers select by script: <see cref="ArabicShaper"/> for <c>arab</c> (cursive
/// joining → positional forms) and <see cref="DefaultShaper"/> for everything else (Latin, Greek,
/// Cyrillic, CJK, and RTL Hebrew). RTL runs are bracket-mirrored and reversed into visual
/// order. There is no normalization pass: NFC input is assumed and marks are reordered by
/// canonical combining class, but nothing is composed/decomposed.</para>
/// </summary>
public static class Shaper
{
    private static readonly Tag ArabicScript = new("arab");
    private static readonly DefaultShaper Default = new();
    private static readonly ArabicShaper Arabic = new();

    /// <summary>
    /// Shape one single-script, single-direction run in place. The buffer must have been filled
    /// with <see cref="ShapeBuffer.AddText"/>; on return it holds glyph ids (visual order for
    /// RTL), clusters, and position deltas in font units. <paramref name="script"/> is an
    /// OpenType script tag (e.g. <c>latn</c>, <c>arab</c>); fonts without the script fall back
    /// to <c>DFLT</c>.
    /// </summary>
    public static void Shape(ShapingFont font, ShapeBuffer buffer, Tag script)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0) return;

        SelectShaper(script).Shape(font, buffer, script);
    }

    /// <summary>The shaper for <paramref name="script"/>. Kept in sync with the plan built by
    /// <see cref="ShapingFont.GetPlan"/>, which resolves the same shaper's feature set.</summary>
    internal static ShaperBase SelectShaper(Tag script)
        => script == ArabicScript ? Arabic : Default;
}
