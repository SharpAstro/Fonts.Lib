namespace SharpAstro.Fonts.Tests;

public class ColrTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "PngDumps");

    static ColrTests() => Directory.CreateDirectory(DumpDir);

    [Fact]
    public void NotoCOLRv1_HasColrAndCpalTables()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        font.HasColorGlyphs.ShouldBeTrue();
        font.Colr.ShouldNotBeNull();
        font.Cpal.ShouldNotBeNull();
        font.Cpal.NumPalettes.ShouldBeGreaterThan((ushort)0);
    }

    [Fact]
    public void NotoCOLRv1_HasV1PaintTrees()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        font.Colr!.HasV1.ShouldBeTrue();
    }

    [Fact]
    public void NotoCOLRv1_RenderColor_ProducesNonEmpty()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        // Find a glyph that actually has color data (iterate charmap until we hit one).
        ColorBitmap? bmp = null;
        for (uint cp = 0x1F300; cp < 0x1F6FF && bmp is null; cp++)
        {
            var gid = font.GetGlyphId(cp);
            if (gid == 0) continue;
            bmp = font.RenderColor(gid, 64f);
        }
        bmp.ShouldNotBeNull("expected at least one COLR glyph in the BMP/Suppl. range");
        bmp.IsEmpty.ShouldBeFalse();
        // Should have at least some non-transparent pixels.
        var hasOpaque = false;
        for (var i = 3; i < bmp.Pixels.Length; i += 4)
            if (bmp.Pixels[i] > 0) { hasOpaque = true; break; }
        hasOpaque.ShouldBeTrue();
    }

    [Fact]
    public void DejaVuSans_HasNoColorGlyphs()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.HasColorGlyphs.ShouldBeFalse();
        font.RenderColor(font.GetGlyphId('A'), 32f).ShouldBeNull();
    }

    [Fact]
    public void DumpNotoColorEmoji_PngSamples()
    {
        // BabelStone has COLR data we can dump for visual inspection.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.BabelStoneXiangqiColour));
        if (!font.HasColorGlyphs) return;

        var written = 0;
        for (uint gid = 1; gid < font.NumGlyphs && written < 20; gid++)
        {
            var bmp = font.RenderColor(gid, 64f);
            if (bmp is null || bmp.IsEmpty) continue;
            PngWriter.WriteRgba(
                System.IO.Path.Combine(DumpDir, $"BabelStone_gid{gid:D4}_64px.png"),
                bmp.Pixels, bmp.Width, bmp.Height);
            written++;
        }
        written.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void DumpNotoCOLRv1_SvgSamples()
    {
        // Same parent folder as outline-only SVG dumps, sub-folder per font.
        var svgDir = System.IO.Path.Combine(AppContext.BaseDirectory, "SvgDumps", "NotoCOLRv1");
        Directory.CreateDirectory(svgDir);
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        int[] codepoints =
        [
            0x1F534, 0x1F7E2, 0x1F7E1, 0x2600, 0x1F525, 0x1F600,
            0x1F308, 0x1F4A1, 0x1F381, 0x2764, 0x1F351, 0x1F34E,
        ];
        var written = 0;
        foreach (var cp in codepoints)
        {
            var gid = font.GetGlyphId((uint)cp);
            if (gid == 0) continue;
            var svg = ColrSvgWriter.ToSvg(font, gid, title: $"U+{cp:X5}");
            if (svg is null) continue;
            File.WriteAllText(System.IO.Path.Combine(svgDir, $"NotoCOLRv1_U+{cp:X5}.svg"), svg);
            svg.ShouldStartWith("<?xml");
            svg.ShouldContain("<path");
            written++;
        }
        written.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void DumpNotoCOLRv1_PngSamples()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        // Spot-check with a mix of bright + colorful emoji so dump output is
        // visually meaningful on dark-themed file managers.
        int[] codepoints =
        [
            0x1F534, 0x1F7E2, 0x1F7E1, 0x1F535, 0x1F7E0, 0x1F7E3, 0x1F7E4,
            0x2600,  0x1F525, 0x1F600, 0x1F606, 0x1F308, 0x1F4A1, 0x1F381,
            0x2764,  0x1F351, 0x1F34E, 0x1F34A,
        ];
        var written = 0;
        foreach (var cp in codepoints)
        {
            var gid = font.GetGlyphId((uint)cp);
            if (gid == 0) continue;
            var bmp = font.RenderColor(gid, 96f);
            if (bmp is null || bmp.IsEmpty) continue;
            PngWriter.WriteRgba(
                System.IO.Path.Combine(DumpDir, $"NotoCOLRv1_U+{cp:X5}_96px.png"),
                bmp.Pixels, bmp.Width, bmp.Height);
            written++;
        }
        written.ShouldBeGreaterThan(0);
    }
}
