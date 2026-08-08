using SharpAstro.Fonts.Hinting;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// A hint program can loop forever — the instruction set has unrestricted backward jumps and
/// nothing obliges a font to satisfy its own exit condition. These tests pin the guarantee that
/// such a program degrades to unhinted rendering instead of hanging the caller.
///
/// <para>The guard is a defence against malformed or hostile bytecode (in this library's main
/// use case it arrives inside someone else's PDF), NOT a workaround for an interpreter defect.
/// It was originally added because NotoSans-Regular's <c>g</c> and <c>x</c> spun forever, but
/// that turned out to be three bugs of ours rather than anything wrong with the font — see
/// <see cref="HintingCorrectnessTests"/>. No bundled face trips the budget any more, so the
/// non-terminating program below is synthesised rather than loaded.</para>
/// </summary>
public class HintingBudgetTests
{
    /// <summary>A two-instruction program that jumps to itself forever:
    /// <c>PUSHW[1] -3</c> then <c>JMPR</c>, which sets ip back to 0 every pass.</summary>
    private static readonly byte[] InfiniteLoop = [0xB8, 0xFF, 0xFD, 0x1C];

    [Fact]
    public void NonTerminatingProgram_HitsTheBudget_InsteadOfHanging()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var interp = font.CreateHintingInterpreter();
        interp.ShouldNotBeNull();
        interp.OnSizeChange(32f, font.UnitsPerEm, font.Prep ?? []);
        interp.ResetInstructionBudget();

        Should.Throw<HintingBudgetExceededException>(
            () => interp.RunGlyphProgram(InfiniteLoop, new Zone(8)));
    }

    /// <summary>The guard must not fire on well-behaved programs — a budget low enough to clip
    /// legitimate hinting would silently degrade every face in the suite.</summary>
    [Theory]
    [InlineData(Fixtures.NotoSans)]
    [InlineData(Fixtures.DejaVuSans)]
    [InlineData(Fixtures.RobotoFlex)]
    public void WellBehavedFaces_StillHint(string face)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(face));
        if (!font.HasHinting) { Assert.Skip($"{face} has no hinting tables"); return; }

        var hintedDiffers = 0;
        var rendered = 0;

        for (var ch = 'A'; ch <= 'Z'; ch++)
        {
            var gid = font.GetGlyphId(ch);
            if (gid == 0) continue;

            var hinted = font.RenderGlyphHinted(gid, 12f);
            var plain = font.RenderGlyph(gid, 12f);
            rendered++;

            // At 12ppem hinting should actually move something on most glyphs; if the budget were
            // clipping these runs, every glyph would come back byte-identical to the plain one.
            if (hinted.Width != plain.Width || hinted.Height != plain.Height
                || !hinted.Alpha.AsSpan().SequenceEqual(plain.Alpha))
            {
                hintedDiffers++;
            }
        }

        rendered.ShouldBeGreaterThan(0);
        hintedDiffers.ShouldBeGreaterThan(0,
            $"{face}: hinting changed no glyph at 12ppem — the budget is probably clipping it");
    }
}
