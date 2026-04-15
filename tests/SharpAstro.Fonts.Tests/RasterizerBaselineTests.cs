namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Regression baselines for the smooth rasterizer. Each test renders a
/// well-known glyph at a fixed PPEM and asserts byte-exact equality with
/// <c>Baselines/{name}.bmp</c>.
///
/// <para>To accept a deliberate change: delete the offending baseline (or
/// run with <c>BASELINE_REGEN=1</c>), re-run, eyeball the regenerated file,
/// commit.</para>
/// </summary>
public class RasterizerBaselineTests
{
    public static TheoryData<string, int, int, string> Cases()
    {
        var data = new TheoryData<string, int, int, string>();
        // (font, codepoint, ppem, baselineName)
        AddSizes(Fixtures.DejaVuSans, 'A', "DejaVu_A");
        AddSizes(Fixtures.DejaVuSans, 'g', "DejaVu_g");
        AddSizes(Fixtures.DejaVuSans, 'Q', "DejaVu_Q");
        AddSizes(Fixtures.DejaVuSans, '8', "DejaVu_8");
        AddSizes(Fixtures.DejaVuSans, 'é', "DejaVu_eacute"); // composite
        // Merida is chess-piece-only — covered by separate (DIR.Lib-ported) tests.

        // CFF coverage via Source Sans 3.
        AddSizes(Fixtures.SourceSans3, 'A', "SourceSans_A");
        AddSizes(Fixtures.SourceSans3, 'g', "SourceSans_g");
        AddSizes(Fixtures.SourceSans3, 'Q', "SourceSans_Q");
        AddSizes(Fixtures.SourceSans3, '8', "SourceSans_8");

        // CJK CFF coverage via NotoSansJP (CFF/OTF).
        AddSizes(Fixtures.NotoSansJP, 0x4E00, "NotoSansJP_4E00"); // 一
        AddSizes(Fixtures.NotoSansJP, 0x6F22, "NotoSansJP_6F22"); // 漢
        AddSizes(Fixtures.NotoSansJP, 0x9AD8, "NotoSansJP_9AD8"); // 高

        void AddSizes(string font, int cp, string baseName)
        {
            foreach (var ppem in new[] { 16, 24, 48 })
                data.Add(font, cp, ppem, $"{baseName}_{ppem:D3}px");
        }
        return data;
    }

    /// <summary>
    /// IVS (Ideographic Variation Sequence) baselines: render the non-default
    /// variant glyph selected by a variation selector and confirm the glyph
    /// differs from the base form.
    /// </summary>
    public static TheoryData<string, int, int, int, string> IvsCases()
    {
        var data = new TheoryData<string, int, int, int, string>();
        // (font, baseCp, variationSelector, ppem, baselineName)
        // U+4FAE (侮) + U+FE00 → GID 15189 (non-default variant)
        data.Add(Fixtures.NotoSansJP, 0x4FAE, 0xFE00, 48, "NotoSansJP_4FAE_FE00_048px");
        // U+3402 (㐂) + U+E0101 → GID 16375 (non-default variant)
        data.Add(Fixtures.NotoSansJP, 0x3402, 0xE0101, 48, "NotoSansJP_3402_E0101_048px");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Render_MatchesBaseline(string fontFile, int codepoint, int ppem, string name)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldBeGreaterThan(0u, $"font {fontFile} has no glyph for U+{codepoint:X4}");

        var bmp = font.RenderGlyph(gid, ppem);
        BaselineAssert.Matches(bmp, name);
    }

    [Theory]
    [MemberData(nameof(IvsCases))]
    public void Render_IVS_MatchesBaseline(string fontFile, int baseCp, int varSelector, int ppem, string name)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));

        var varGid = font.GetGlyphId((uint)baseCp, (uint)varSelector);
        varGid.ShouldBeGreaterThan(0u,
            $"font {fontFile} has no IVS glyph for U+{baseCp:X4} + U+{varSelector:X4}");

        // The IVS glyph should differ from the base glyph.
        var baseGid = font.GetGlyphId((uint)baseCp);
        varGid.ShouldNotBe(baseGid,
            $"IVS U+{baseCp:X4}+U+{varSelector:X4} should select a non-default glyph");

        var bmp = font.RenderGlyph(varGid, ppem);
        BaselineAssert.Matches(bmp, name);
    }
}
