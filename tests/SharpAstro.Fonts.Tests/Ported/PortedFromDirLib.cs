using System.Text;
using SharpAstro.Fonts.Tables.Cmap;

namespace SharpAstro.Fonts.Tests.Ported;

/// <summary>
/// DIR.Lib parity tests, ported to the SharpAstro.Fonts API. Replaces the
/// original placeholder skips. The originals live at
/// <c>DIR.Lib/src/DIR.Lib.Tests/{CmapDumpTests,CmapLookupOrderTests,FontInspectionTests,SubsetFontGlyphTests,RenderAcceptanceTests}.cs</c>
/// and exercised <c>FreeTypeGlyphRasterizer.RasterizeGlyphWithCharCode</c>;
/// these versions use <see cref="OpenTypeFont.GetGlyphId(uint, uint, GlyphMapHint)"/>
/// + <see cref="OpenTypeFont.RenderGlyph(uint, float, int)"/>.
/// </summary>
public class PortedFromDirLib
{
    [Theory]
    [InlineData("XXTIIT_Arial_subset.ttf", new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData("Tahoma_subset.ttf",       new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData("ISOCPEUR_subset.ttf",     new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void CmapDumpTests_AllHints(string fontFile, uint[] charCodes)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));

        var hints = new[] { GlyphMapHint.Auto, GlyphMapHint.EmbeddedSubset,
                            GlyphMapHint.CharCodeIsGID, GlyphMapHint.Unicode };

        var sb = new StringBuilder();
        sb.AppendLine($"=== {fontFile} ===");
        sb.AppendLine("cc  | Auto       | EmbSubset  | CharIsGID  | Unicode");

        // For each (charCode, hint) record whether we got a non-zero glyph id.
        // Assert that EmbeddedSubset finds at least one glyph (the original
        // bug was that hints other than CharCodeIsGID returned 0 for subset
        // fonts).
        var embeddedHits = 0;
        foreach (var cc in charCodes)
        {
            sb.Append(cc.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).Append(" |");
            foreach (var hint in hints)
            {
                var gid = font.GetGlyphId(cc, cc, hint);
                if (hint == GlyphMapHint.EmbeddedSubset && gid != 0) embeddedHits++;
                sb.Append($" gid={gid,4}    |");
            }
            sb.AppendLine();
        }
        // Diagnostic dump for eyeballing.
        var outPath = System.IO.Path.Combine(AppContext.BaseDirectory,
            $"cmap_dump_{System.IO.Path.GetFileNameWithoutExtension(fontFile)}.txt");
        File.WriteAllText(outPath, sb.ToString());

        embeddedHits.ShouldBeGreaterThan(0,
            $"EmbeddedSubset hint found no glyphs for {fontFile} — regression of the cmap-lookup-order fix");
    }

    [Fact]
    public void CmapLookupOrder_EmbeddedSubset_FindsViaSymbolPUA()
    {
        // XXTIIT+Arial subset uses MS Symbol encoding (PUA offset U+F000+charCode).
        // EmbeddedSubset must find the glyph via that lookup, NOT via the wrong
        // Mac Roman cmap that Auto would also try (and which mismaps subset chars).
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.XXTIIT_Arial_Subset));
        var gid = font.GetGlyphId(codepoint: 'w', charCode: 1, GlyphMapHint.EmbeddedSubset);
        gid.ShouldBeGreaterThan(0u);

        // Sanity: rendering the result should produce a non-empty bitmap.
        var bmp = font.RenderGlyph(gid, 24f);
        bmp.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void FontInspection_DumpFontCmap_And_Glyphs()
    {
        // Diagnostic dump (non-failing) — useful when poking at an unfamiliar
        // PDF subset to figure out the right hint.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.XXTIIT_Arial_Subset));
        var sb = new StringBuilder();
        sb.AppendLine($"NumGlyphs={font.NumGlyphs}, UnitsPerEm={font.UnitsPerEm}");
        sb.AppendLine($"Cmap subtables: {font.Cmap.Subtables.Count}");
        foreach (var s in font.Cmap.Subtables)
            sb.AppendLine($"  ({s.PlatformId}, {s.EncodingId}) format={s.Format}");

        sb.AppendLine("\n=== PUA U+F000+cc ===");
        for (uint cc = 1; cc <= 20; cc++)
        {
            var gid = font.GetGlyphId(0xF000u + cc);
            sb.AppendLine($"  U+{0xF000+cc:X4}: gid={gid}");
        }
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,
            "font_inspection.txt"), sb.ToString());
    }

    [Fact]
    public void SubsetFont_CharCodeAsGID_ProducesNonEmptyGlyph()
    {
        // XXTIIT+Arial: charCode 1='w', 2='.', 3='a', 4='u', 5='t', ...
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.XXTIIT_Arial_Subset));
        (uint cc, char ch)[] knownGlyphs =
        [
            (1, 'w'), (2, '.'), (3, 'a'), (4, 'u'), (5, 't'),
            (6, 'o'), (7, 'd'), (8, 'e'), (9, 's'), (10, 'k'),
        ];
        foreach (var (cc, ch) in knownGlyphs)
        {
            var gid = font.GetGlyphId(ch, cc, GlyphMapHint.EmbeddedSubset);
            gid.ShouldBeGreaterThan(0u, $"cc={cc} '{ch}' produced no glyph");
            var bmp = font.RenderGlyph(gid, 24f);
            bmp.IsEmpty.ShouldBeFalse($"cc={cc} '{ch}' rendered empty");
        }
    }

    [Fact]
    public void RenderAcceptance_ColorEmoji()
    {
        // COLR v1 colored render acceptance — verify a known bright emoji
        // produces non-trivial colored output.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var gid = font.GetGlyphId(0x1F534); // 🔴 RED CIRCLE
        gid.ShouldBeGreaterThan(0u);

        var bmp = font.RenderColor(gid, 64f);
        bmp.ShouldNotBeNull();
        bmp.IsEmpty.ShouldBeFalse();

        // Average opaque pixel should be predominantly red.
        int rSum = 0, gSum = 0, bSum = 0, count = 0;
        for (var i = 0; i < bmp.Pixels.Length; i += 4)
        {
            if (bmp.Pixels[i + 3] > 0)
            {
                rSum += bmp.Pixels[i];
                gSum += bmp.Pixels[i + 1];
                bSum += bmp.Pixels[i + 2];
                count++;
            }
        }
        count.ShouldBeGreaterThan(0);
        var avgR = rSum / count;
        var avgG = gSum / count;
        var avgB = bSum / count;
        avgR.ShouldBeGreaterThan(avgG, "RED CIRCLE should have R > G");
        avgR.ShouldBeGreaterThan(avgB, "RED CIRCLE should have R > B");
    }
}
