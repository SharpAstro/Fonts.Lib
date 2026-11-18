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

    [Theory]
    [InlineData(0x1F534, 150, 0, 0)]   // 🔴 RED CIRCLE — dominant red
    [InlineData(0x1F7E2, 0, 100, 0)]   // 🟢 GREEN CIRCLE — dominant green
    [InlineData(0x1F7E1, 150, 150, 0)]  // 🟡 YELLOW CIRCLE — red+green, low blue
    public void NotoCOLRv1_BrightEmoji_HasPlausibleColor(int codepoint,
        int minR, int minG, int minB)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldBeGreaterThan(0u);

        var bmp = font.RenderColor(gid, 96f);
        bmp.ShouldNotBeNull();
        bmp.IsEmpty.ShouldBeFalse();

        long rSum = 0, gSum = 0, bSum = 0;
        var opaqueCount = 0;
        for (var i = 0; i < bmp.Pixels.Length; i += 4)
        {
            if (bmp.Pixels[i + 3] > 0)
            {
                rSum += bmp.Pixels[i];
                gSum += bmp.Pixels[i + 1];
                bSum += bmp.Pixels[i + 2];
                opaqueCount++;
            }
        }
        opaqueCount.ShouldBeGreaterThan(0, "emoji should have opaque pixels");
        var avgR = (int)(rSum / opaqueCount);
        var avgG = (int)(gSum / opaqueCount);
        var avgB = (int)(bSum / opaqueCount);
        avgR.ShouldBeGreaterThanOrEqualTo(minR, $"U+{codepoint:X5} avgR={avgR}");
        avgG.ShouldBeGreaterThanOrEqualTo(minG, $"U+{codepoint:X5} avgG={avgG}");
        avgB.ShouldBeGreaterThanOrEqualTo(minB, $"U+{codepoint:X5} avgB={avgB}");
    }

    // PaintComposite in a real font. A walk of this font's paint graphs found two modes: SrcIn in 296 glyphs (the
    // laptop's screen is a gradient kept to the screen's shape) and SoftLight in 262 (the flags' shading). Until 2.0
    // the compositing mixed colours of 0 to 255 with alphas of 0 to 1, so every channel that was not zero came out
    // full. The values here were read off the picture after looking at it.
    [Fact]
    public void NotoCOLRv1_ASrcInComposite_KeepsTheSourcesColours()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var bmp = font.RenderColor(font.GetGlyphId(0x1F4BB), 160f);   // 💻
        bmp.ShouldNotBeNull();

        // The screen: a dark gradient, bluer at the top left than at the bottom right, and opaque.
        var topLeft = PixelAt(bmp, 0.3f, 0.2f);
        var bottomRight = PixelAt(bmp, 0.7f, 0.5f);
        topLeft.A.ShouldBe((byte)255);
        bottomRight.A.ShouldBe((byte)255);
        ((int)topLeft.R).ShouldBeInRange(46, 66);
        ((int)topLeft.G).ShouldBeInRange(82, 102);
        ((int)topLeft.B).ShouldBeInRange(91, 111);
        ((int)bottomRight.R).ShouldBeInRange(56, 76);
        ((int)bottomRight.G).ShouldBeInRange(56, 76);
        ((int)bottomRight.B).ShouldBeInRange(56, 76);

        // Outside the screen's shape the gradient is not drawn at all.
        PixelAt(bmp, 0.02f, 0.2f).A.ShouldBe((byte)0);
    }

    [Fact]
    public void NotoCOLRv1_ASoftLightComposite_KeepsTheFlagsColours()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var bmp = font.RenderColor(3750, 160f);   // the flag of Albania, a ligature with no code point of its own
        bmp.ShouldNotBeNull();

        // The red field away from the shaded edges, and the black eagle in the middle.
        var field = PixelAt(bmp, 0.3f, 0.2f);
        ((int)field.R).ShouldBeInRange(223, 233);
        ((int)field.G).ShouldBeInRange(25, 35);
        ((int)field.B).ShouldBeInRange(27, 37);
        field.A.ShouldBe((byte)255);
        var eagle = PixelAt(bmp, 0.5f, 0.5f);
        ((int)eagle.R + eagle.G + eagle.B).ShouldBeLessThan(30);
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(ColorBitmap bmp, float fx, float fy)
    {
        var i = ((int)(bmp.Height * fy) * bmp.Width + (int)(bmp.Width * fx)) * 4;
        return (bmp.Pixels[i], bmp.Pixels[i + 1], bmp.Pixels[i + 2], bmp.Pixels[i + 3]);
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
