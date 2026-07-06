using System.Collections.Generic;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping;
using Shouldly;
using Xunit;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// <see cref="BidiScriptItemizer"/>: resolves embedding levels then emits single-level,
/// single-script runs in visual order. The key behaviours are (a) segmentation by level AND script
/// and (b) L2 run reordering, so that appending the shaped runs places mixed-direction text
/// correctly.
/// </summary>
public class BidiScriptItemizerTests
{
    private static readonly Tag Latn = new("latn");
    private static readonly Tag Hebr = new("hebr");

    [Fact]
    public void PlainLatin_SingleLtrRun()
    {
        var runs = new List<ScriptRun>();
        var para = BidiScriptItemizer.Itemize("abc", BidiAlgorithm.AutoLevel, runs);

        para.ShouldBe((byte)0);
        runs.Count.ShouldBe(1);
        runs[0].ShouldBe(new ScriptRun(0, 3, Latn, ShapeDirection.LeftToRight));
    }

    [Fact]
    public void LatinThenHebrew_LtrParagraph_TwoRunsInOrder()
    {
        // "abc" + Hebrew alef-bet-gimel. LTR paragraph: Latin stays first, the Hebrew run follows
        // (its glyphs get reversed by the shaper); the run ORDER is unchanged here.
        var runs = new List<ScriptRun>();
        var para = BidiScriptItemizer.Itemize("abcאבג", 0, runs);

        para.ShouldBe((byte)0);
        runs.Count.ShouldBe(2);
        runs[0].ShouldBe(new ScriptRun(0, 3, Latn, ShapeDirection.LeftToRight));
        runs[1].ShouldBe(new ScriptRun(3, 3, Hebr, ShapeDirection.RightToLeft));
    }

    [Fact]
    public void HebrewLatinHebrew_RtlParagraph_RunsReorderedVisually()
    {
        // alef, 'b', gimel in an RTL paragraph. The two Hebrew runs are RTL (level 1), the Latin is
        // LTR-in-RTL (level 2). L2 reverses the run order, so visually (left→right) the runs are:
        // gimel (logical index 2), then 'b' (1), then alef (0).
        var runs = new List<ScriptRun>();
        var para = BidiScriptItemizer.Itemize("אbג", 1, runs);

        para.ShouldBe((byte)1);
        runs.Count.ShouldBe(3);
        runs[0].ShouldBe(new ScriptRun(2, 1, Hebr, ShapeDirection.RightToLeft));  // gimel
        runs[1].ShouldBe(new ScriptRun(1, 1, Latn, ShapeDirection.LeftToRight));  // 'b'
        runs[2].ShouldBe(new ScriptRun(0, 1, Hebr, ShapeDirection.RightToLeft));  // alef
    }

    [Fact]
    public void PureHebrew_AutoResolvesRtl()
    {
        var runs = new List<ScriptRun>();
        var para = BidiScriptItemizer.Itemize("אבג", BidiAlgorithm.AutoLevel, runs);

        para.ShouldBe((byte)1);
        runs.Count.ShouldBe(1);
        runs[0].ShouldBe(new ScriptRun(0, 3, Hebr, ShapeDirection.RightToLeft));
    }
}
