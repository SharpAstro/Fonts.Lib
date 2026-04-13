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

        void AddSizes(string font, int cp, string baseName)
        {
            foreach (var ppem in new[] { 16, 24, 48 })
                data.Add(font, cp, ppem, $"{baseName}_{ppem:D3}px");
        }
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
}
