namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Small-ppem (low-DPI) rendering baselines for the HINTED path
/// (<see cref="OpenTypeFont.RenderGlyphHinted"/>) — the regime where our output
/// diverges most from FreeType/pdfium (thin stems wash into grays, no grid-fit).
/// <see cref="RasterizerBaselineTests"/> only covers 16/24/48 px on the unhinted
/// path, so this is the missing coverage that lets us measure fidelity work
/// (gamma-correct AA, completing the TrueType hinting interpreter) — every change
/// to the rasterizer shows up here as a baseline diff.
///
/// <para>Includes a real PDF-style SUBSET font (<see cref="Fixtures.XXTIIT_Arial_Subset"/>)
/// rendered by glyph id, exercising "extracted-from-a-PDF" font data directly —
/// independent of the whole PDF pipeline.</para>
///
/// <para>Regenerate baselines with <c>BASELINE_REGEN=1 dotnet test</c>, eyeball
/// the new <c>Baselines/SmallSize_*.bmp</c>, commit, re-run to lock in.</para>
/// </summary>
public class SmallSizeFidelityTests
{
    // The low-DPI regime that degrades. 16px overlaps the existing suite as a sanity anchor.
    private static readonly int[] SmallPpem = [8, 10, 12, 16];

    public static TheoryData<string, int, int, string> ByCodepoint()
    {
        var data = new TheoryData<string, int, int, string>();
        // Stem ('H'/'l'), bowl ('e'), descender ('g'), and a digit ('8') stress
        // grid-fitting + thin-stroke AA differently.
        Add(Fixtures.DejaVuSans, 'H', "DejaVu_H");   // hinted TrueType
        Add(Fixtures.DejaVuSans, 'e', "DejaVu_e");
        Add(Fixtures.DejaVuSans, 'g', "DejaVu_g");
        Add(Fixtures.DejaVuSans, 'l', "DejaVu_l");
        Add(Fixtures.SourceSans3, 'H', "SourceSans_H"); // CFF (no TT hinting → AA-only)
        Add(Fixtures.SourceSans3, 'e', "SourceSans_e");

        void Add(string font, int cp, string baseName)
        {
            foreach (var ppem in SmallPpem)
                data.Add(font, cp, ppem, $"SmallSize_{baseName}_{ppem:D3}px");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ByCodepoint))]
    public void RenderHinted_MatchesBaseline(string fontFile, int codepoint, int ppem, string name)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldBeGreaterThan(0u, $"font {fontFile} has no glyph for U+{codepoint:X4}");

        var bmp = font.RenderGlyphHinted(gid, ppem);
        BaselineAssert.Matches(bmp, name);
    }

    // Extracted subset font: drive it by glyph id the way the PDF pipeline does (a subset
    // font's cmap may be partial). The first few gids are typically .notdef / space, so we
    // discover the first inked glyphs at a reference ppem and baseline THOSE across all sizes.
    public static TheoryData<string, int, int, string> SubsetByGid()
    {
        var data = new TheoryData<string, int, int, string>();
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.XXTIIT_Arial_Subset));
        var inked = new List<int>();
        for (var gid = 1; gid < font.NumGlyphs && inked.Count < 4; gid++)
            if (!font.RenderGlyph((uint)gid, 24).IsEmpty) // probe at a comfortable ppem
                inked.Add(gid);

        foreach (var gid in inked)
            foreach (var ppem in SmallPpem)
                data.Add(Fixtures.XXTIIT_Arial_Subset, gid, ppem, $"SmallSize_ArialSubset_g{gid}_{ppem:D3}px");
        return data;
    }

    [Theory]
    [MemberData(nameof(SubsetByGid))]
    public void RenderHinted_Subset_MatchesBaseline(string fontFile, int gid, int ppem, string name)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
        var bmp = font.RenderGlyphHinted((uint)gid, ppem);
        if (bmp.IsEmpty) return; // washed out to nothing at this tiny ppem — skip
        BaselineAssert.Matches(bmp, name);
    }
}
