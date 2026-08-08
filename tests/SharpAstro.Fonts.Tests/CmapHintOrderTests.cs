using SharpAstro.Fonts.Tables.Cmap;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Pins the strategy ORDER inside <see cref="CmapTable.GetGlyphIdHinted"/>, which is what decides
/// whether a PDF subset font renders the glyphs its producer meant. The cmaps here are synthesized
/// rather than read from a fixture face: the property under test is *which subtables a face
/// carries*, and a real face pins that only incidentally. The (1,0)-only shape in particular is
/// what macOS Quartz emits for simple-TrueType subsets, and no bundled fixture has it.
/// </summary>
public class CmapHintOrderTests
{
    /// <summary>Glyph count the synthetic faces claim — big enough that a small char code is a
    /// plausible direct-GID guess, so a test only passes by preferring a real subtable.</summary>
    private const ushort NumGlyphs = 224;

    [Fact]
    public void EmbeddedSubset_MacRomanOnlyFace_MapsThroughItRatherThanGuessing()
    {
        // The Quartz shape: one (1,0) subtable and nothing else. Before this was tried, the lookup
        // fell straight to the direct-GID fallback and returned the char code itself — which lands
        // on an unrelated in-range glyph, the collation-ordered-Hangul bug.
        var cmap = Build((1, 0, firstCode: 33, gids: [7]));

        var gid = cmap.GetGlyphIdHinted(codepoint: 0, charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs);

        gid.ShouldBe(7u);
        gid.ShouldNotBe(33u, "the char code itself is the blind guess this subtable exists to replace");
    }

    [Fact]
    public void EmbeddedSubset_SymbolPuaStillWins_OverMacRoman()
    {
        // Revit's XXTIIT+Arial / Tahoma / ISOCPEUR subsets carry BOTH, and (3,0) PUA is the correct
        // one for them. Mac Roman ranks below it, so adding it changed nothing for those faces.
        var cmap = Build((3, 0, firstCode: 0xF021, gids: [5]),
                         (1, 0, firstCode: 33, gids: [7]));

        cmap.GetGlyphIdHinted(codepoint: 0, charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs)
            .ShouldBe(5u);
    }

    [Fact]
    public void EmbeddedSubset_SymbolRawCodeStillWins_OverMacRoman()
    {
        // mPDF-style CJK subsets key (3,0) by the raw code rather than PUA-offset.
        var cmap = Build((3, 0, firstCode: 33, gids: [5]),
                         (1, 0, firstCode: 33, gids: [7]));

        cmap.GetGlyphIdHinted(codepoint: 0, charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs)
            .ShouldBe(5u);
    }

    [Fact]
    public void EmbeddedSubset_GenuineUnicodeStillWins_OverMacRoman()
    {
        var cmap = Build((3, 1, firstCode: 'A', gids: [9]),
                         (1, 0, firstCode: 33, gids: [7]));

        cmap.GetGlyphIdHinted('A', charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs)
            .ShouldBe(9u);
    }

    [Fact]
    public void EmbeddedSubset_DirectGidRemainsTheLastResort()
    {
        // (1,0) present but silent on this code — the Identity-subset guess still applies.
        var cmap = Build((1, 0, firstCode: 99, gids: [7]));

        cmap.GetGlyphIdHinted(codepoint: 0, charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs)
            .ShouldBe(33u);
    }

    [Fact]
    public void EmbeddedSubset_OutOfRangeMacRomanGid_FallsThroughToDirectGid()
    {
        // A subset whose cmap outlives its glyf: the subtable answers, but past the glyph count.
        var cmap = Build((1, 0, firstCode: 33, gids: [500]));

        cmap.GetGlyphIdHinted(codepoint: 0, charCode: 33, GlyphMapHint.EmbeddedSubset, NumGlyphs)
            .ShouldBe(33u);
    }

    [Fact]
    public void EmbeddedSubset_CodePastTheSubsetEnd_ResolvesThroughMacRomanInsteadOfNothing()
    {
        // The other half of the Quartz bug: a 4-glyph Latin/punctuation subset still uses codes
        // like 44 (comma), which the direct-GID guess rejects as out of range — so the glyph
        // vanished entirely. Commas, periods and page numbers went missing for exactly this.
        var cmap = Build((1, 0, firstCode: 44, gids: [2]));

        cmap.GetGlyphIdHinted(codepoint: 0, charCode: 44, GlyphMapHint.EmbeddedSubset, numGlyphs: 4)
            .ShouldBe(2u);
    }

    /// <summary>
    /// A cmap table holding one format-6 ("trimmed mapping") subtable per entry. Format 6 rather
    /// than format 0 so a subtable can be keyed above U+00FF, which the (3,0) PUA convention needs.
    /// </summary>
    private static CmapTable Build(params (ushort plat, ushort enc, ushort firstCode, ushort[] gids)[] subs)
    {
        var header = 4 + 8 * subs.Length;
        var lengths = subs.Select(s => 10 + 2 * s.gids.Length).ToArray();
        var buf = new byte[header + lengths.Sum()];
        var w = 0;
        void U16(ushort v) { buf[w++] = (byte)(v >> 8); buf[w++] = (byte)v; }
        void U32(uint v)
        {
            buf[w++] = (byte)(v >> 24); buf[w++] = (byte)(v >> 16);
            buf[w++] = (byte)(v >> 8); buf[w++] = (byte)v;
        }

        U16(0);                                  // version
        U16((ushort)subs.Length);
        var offset = header;
        for (var i = 0; i < subs.Length; i++)
        {
            U16(subs[i].plat);
            U16(subs[i].enc);
            U32((uint)offset);
            offset += lengths[i];
        }
        for (var i = 0; i < subs.Length; i++)
        {
            U16(6);                              // format
            U16((ushort)lengths[i]);
            U16(0);                              // language
            U16(subs[i].firstCode);
            U16((ushort)subs[i].gids.Length);
            foreach (var gid in subs[i].gids) U16(gid);
        }
        return CmapTable.Parse(buf);
    }
}
