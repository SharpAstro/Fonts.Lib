namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Regression cover for three interpreter defects that compounded into visibly broken hinting,
/// and that nothing in the suite detected because each one masked the next:
///
/// <list type="number">
/// <item><c>Zone</c>'s constructor never set <c>PointCount</c>. The glyph zone gets its count
/// assigned by <c>HintingPipeline</c>, but the twilight zone is only ever constructed — so it
/// reported 0 points and every twilight bounds-check failed, silently turning zone 0 into a
/// no-op. Fonts stage reference positions there, so a whole class of hinting did nothing.</item>
/// <item><c>ALIGNRP</c> returned on that failed bounds-check <em>without popping</em> its
/// operands. Fonts drive ALIGNRP from a LOOPCALL'd helper, so the surplus accumulated until the
/// enclosing "call until the stack drains" loop could never reach its exit depth — an outright
/// hang on NotoSans-Regular's <c>g</c> and <c>x</c>.</item>
/// <item>The <c>cvt </c> table was read as <c>ushort</c>, but it is an array of FWORD —
/// <em>signed</em> int16. 26 of NotoSans-Regular's 150 control values are negative, and each
/// became a huge positive. Defect 1 hid this: with twilight dead, the corrupt values never
/// reached a point position.</item>
/// </list>
///
/// <para>The sweep in <see cref="HintedGlyphs_StayInProportionToUnhinted"/> is the general guard.
/// Grid-fitting moves outlines by a pixel or two; anything further apart is corruption, and that
/// single assertion would have caught all three.</para>
/// </summary>
public class HintingCorrectnessTests
{
    /// <summary>The catch-all. Hinting nudges an outline onto the pixel grid — it never changes
    /// its size materially. Before the fixes this found 139 blown-up (glyph, ppem) pairs in
    /// NotoSans-Regular, the worst 80x taller than the unhinted glyph.</summary>
    [Theory]
    [InlineData(Fixtures.NotoSans)]
    [InlineData(Fixtures.DejaVuSans)]
    public void HintedGlyphs_StayInProportionToUnhinted(string face)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(face));
        if (!font.HasHinting) { Assert.Skip($"{face} has no hinting tables"); return; }

        var offenders = new List<string>();
        var compared = 0;

        foreach (var ppem in new[] { 12f, 24f, 32f, 72f })
        {
            for (uint gid = 1; gid < Math.Min((int)font.NumGlyphs, 400); gid++)
            {
                var hinted = font.RenderGlyphHinted(gid, ppem);
                var plain = font.RenderGlyph(gid, ppem);
                if (plain.IsEmpty) continue;
                compared++;

                // 3px of slack: enough for grid-fitting plus a rounded edge either side, far
                // tighter than any of the corruption modes above.
                if (Math.Abs(hinted.Width - plain.Width) > 3 || Math.Abs(hinted.Height - plain.Height) > 3)
                    offenders.Add($"gid {gid} @{ppem}px: hinted {hinted.Width}x{hinted.Height}, "
                                + $"unhinted {plain.Width}x{plain.Height}");
            }
        }

        compared.ShouldBeGreaterThan(0);
        offenders.ShouldBeEmpty($"{face}: {offenders.Count} of {compared} hinted glyphs are out "
                              + $"of proportion:\n  {string.Join("\n  ", offenders.Take(10))}");
    }

    /// <summary>Defect 3, pinned at the source. Reading these unsigned is silent — the values
    /// stay in range and only go wrong once something scales them.</summary>
    [Fact]
    public void CvtTable_IsReadAsSignedFwords()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSans));
        var cvt = font.CvtFunits;
        cvt.ShouldNotBeNull();

        cvt.Count(v => v < 0).ShouldBe(26, "NotoSans-Regular has 26 negative control values");
        cvt[111].ShouldBe((short)-240);

        // A control value is a distance in font units, so |v| stays well inside one em.
        // Read unsigned, the negatives came back as ~65000 — dozens of ems.
        foreach (var v in cvt)
            Math.Abs((int)v).ShouldBeLessThanOrEqualTo(2 * font.UnitsPerEm);
    }

    /// <summary>Defect 2. These two glyphs hung the process outright; the instruction budget
    /// contained that into a silent fallback to unhinted. Now they simply hint.</summary>
    [Theory]
    [InlineData('g')]
    [InlineData('x')]
    public void NotoSans_GlyphsThatUsedToSpinForever_NowHint(char ch)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSans));
        var gid = font.GetGlyphId(ch);

        for (var ppem = 8f; ppem <= 128f; ppem++)
        {
            font.RenderGlyphHinted(gid, ppem);
            font.HintingBudgetFailures.ContainsKey((gid, ppem)).ShouldBeFalse(
                $"'{ch}' still exhausts the instruction budget at {ppem}ppem");
        }
    }

    /// <summary>Defect 1's most visible symptom: <c>t</c> hinted to a 1-pixel sliver at 87, 90
    /// and 91 ppem while 86, 88, 89 and 92 were fine, so the letter vanished mid-word. The
    /// program terminated and returned a valid-but-degenerate outline, so nothing fell back.</summary>
    [Fact]
    public void NotoSans_LowercaseT_DoesNotCollapse()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSans));
        var gid = font.GetGlyphId('t');

        for (var ppem = 84f; ppem <= 94f; ppem++)
        {
            var hinted = font.RenderGlyphHinted(gid, ppem);
            var plain = font.RenderGlyph(gid, ppem);
            hinted.Height.ShouldBeGreaterThan(plain.Height - 3,
                $"'t' collapsed at {ppem}ppem: hinted {hinted.Height}px vs unhinted {plain.Height}px");
        }
    }
}
