using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Tables.Cmap;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// A bare CFF program (no SFNT wrapper) is what a PDF embeds for a CIDFontType0 in
/// <c>/FontFile3</c>. <see cref="OpenTypeFont.Load"/> must accept it directly — glyph count,
/// units-per-em and outlines all come from the CFF itself. The strong invariant: loading the
/// bare <c>CFF </c> table sliced out of an OTF must render byte-identically to loading the whole
/// OTF, because the outline data is the very same bytes. Fixtures are the bundled SIL-OFL OTFs
/// (SourceSans3 = non-CID, NotoSansJP = CID-keyed), so no proprietary/customer font is committed.
/// </summary>
public class BareCffTests
{
    // Slice the raw 'CFF ' table out of an SFNT-wrapped OTF — that slice IS a bare CFF program,
    // exactly like a PDF /FontFile3.
    private static byte[] ExtractCffTable(string fixtureName)
    {
        var otf = OpenTypeFont.LoadFromFile(Fixtures.Path(fixtureName));
        otf.TryGetTable(new Tag("CFF "), out var cff).ShouldBeTrue($"{fixtureName} should have a CFF table");
        return cff.ToArray();
    }

    [Theory]
    [InlineData(Fixtures.SourceSans3)]
    [InlineData(Fixtures.NotoSansJP)]
    public void BareCff_Loads_WithMatchingMetrics(string fixtureName)
    {
        var otf = OpenTypeFont.LoadFromFile(Fixtures.Path(fixtureName));
        var bare = OpenTypeFont.Load(ExtractCffTable(fixtureName));

        bare.HasCffOutlines.ShouldBeTrue();
        bare.Glyf.ShouldBeNull();
        // Glyph count and em-square come from the CFF, so they must match the wrapped face.
        bare.NumGlyphs.ShouldBe(otf.NumGlyphs);
        bare.UnitsPerEm.ShouldBe(otf.UnitsPerEm);
    }

    [Theory]
    [InlineData(Fixtures.SourceSans3)]
    [InlineData(Fixtures.NotoSansJP)]
    public void BareCff_RendersIdenticalPixels_ToWrappedFace(string fixtureName)
    {
        var otf = OpenTypeFont.LoadFromFile(Fixtures.Path(fixtureName));
        var bare = OpenTypeFont.Load(ExtractCffTable(fixtureName));

        // Sample GIDs across the range; the outline bytes are shared, so every rendered
        // bitmap must be pixel-identical. A drop-out or misparse in the bare path shows here.
        var n = otf.NumGlyphs;
        int[] gids = [1, n / 7, n / 3, n / 2, (n * 3) / 4, n - 1];
        var rendered = 0;
        foreach (var gid in gids)
        {
            if (gid <= 0 || gid >= n) continue;
            var a = otf.RenderGlyph((uint)gid, 48f);
            var b = bare.RenderGlyph((uint)gid, 48f);
            b.Width.ShouldBe(a.Width, $"width mismatch at gid {gid}");
            b.Height.ShouldBe(a.Height, $"height mismatch at gid {gid}");
            b.Alpha.ShouldBe(a.Alpha, $"pixel mismatch at gid {gid}");
            if (a.Width > 0) rendered++;
        }
        rendered.ShouldBeGreaterThan(0, "at least one sampled glyph must have ink (else the test proves nothing)");
    }

    [Fact]
    public void BareCidCff_ResolvesGlyphsByCid_ThroughCharset()
    {
        // NotoSansJP is a CID-keyed CFF. For a CIDFontType0 the PDF hands over a CID and the
        // renderer must resolve it to a GID via the CFF charset — NOT assume CID==GID. Verify the
        // round-trip: take a GID, read its CID from the charset, and confirm GetGlyphId maps that
        // CID back to the same GID. Also confirm the mapping is genuinely charset-driven (at least
        // one sampled glyph has CID != GID — proving the identity shortcut would be wrong here).
        var bare = OpenTypeFont.Load(ExtractCffTable(Fixtures.NotoSansJP));
        bare.Cff.ShouldNotBeNull();
        bare.Cff!.IsCidKeyed.ShouldBeTrue();
        bare.Cff.Charset.ShouldNotBeNull();

        var n = bare.NumGlyphs;
        int[] gids = [1, n / 5, n / 2, (n * 4) / 5, n - 1];
        var sawNonIdentity = false;
        foreach (var gid in gids)
        {
            if (gid <= 0 || gid >= n) continue;
            var cid = bare.Cff.Charset!.GetSid((uint)gid);
            if (cid == 0) continue; // .notdef / unmapped — skip
            if (cid != gid) sawNonIdentity = true;
            bare.GetGlyphId(0, cid, GlyphMapHint.CharCodeIsGID)
                .ShouldBe((uint)gid, $"CID {cid} should resolve back to GID {gid}");
        }
        sawNonIdentity.ShouldBeTrue("expected at least one CID != GID (charset must be exercised, not bypassed)");
    }
}
