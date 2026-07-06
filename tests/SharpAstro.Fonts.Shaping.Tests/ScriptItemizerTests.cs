using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Script itemization (level B) and the Script property table it rides on. Verifies run grouping,
/// Common/Inherited attachment (preceding, and forward when leading), and script-implied direction.
/// The non-ASCII literals are the exact codepoints their comments name — notably "á" is
/// 'a' + U+0301 (two code units), not a precomposed glyph — so the char offsets asserted are exact.
/// </summary>
public class ScriptItemizerTests
{
    private static readonly Tag Latn = new("latn");
    private static readonly Tag Arab = new("arab");
    private static readonly Tag Hebr = new("hebr");

    // ---- Script property table ----

    [Fact]
    public void Script_Get_ResolvesKnownScripts()
    {
        Script.Get('a').ShouldBe(Latn);
        Script.Get('Z').ShouldBe(Latn);
        Script.Get(0x0628).ShouldBe(Arab);      // Arabic beh
        Script.Get(0x05D0).ShouldBe(Hebr);      // Hebrew alef
        Script.Get('5').ShouldBe(Script.Common);      // digit
        Script.Get('.').ShouldBe(Script.Common);      // punctuation
        Script.Get(0x0301).ShouldBe(Script.Inherited); // combining acute
    }

    [Fact]
    public void Script_IsRightToLeft_OnlyForRtlScripts()
    {
        Script.IsRightToLeft(Arab).ShouldBeTrue();
        Script.IsRightToLeft(Hebr).ShouldBeTrue();
        Script.IsRightToLeft(Latn).ShouldBeFalse();
        Script.IsRightToLeft(new Tag("grek")).ShouldBeFalse();
    }

    // ---- itemization ----

    [Fact]
    public void Empty_ProducesNoRuns()
        => ScriptItemizer.Itemize("").ShouldBeEmpty();

    [Fact]
    public void PureLatin_IsOneLtrRun()
        => ScriptItemizer.Itemize("hello").ShouldBe([new ScriptRun(0, 5, Latn, ShapeDirection.LeftToRight)]);

    [Fact]
    public void PureArabic_IsOneRtlRun()
        => ScriptItemizer.Itemize("عرب") // ain-reh-beh
            .ShouldBe([new ScriptRun(0, 3, Arab, ShapeDirection.RightToLeft)]);

    [Fact]
    public void PureHebrew_IsOneRtlRun()
        => ScriptItemizer.Itemize("שלום") // shin-lamed-vav-finalmem
            .ShouldBe([new ScriptRun(0, 4, Hebr, ShapeDirection.RightToLeft)]);

    [Fact]
    public void ScriptChange_SplitsRuns()
        => ScriptItemizer.Itemize("abعر") // "ab" + ain-reh
            .ShouldBe([
                new ScriptRun(0, 2, Latn, ShapeDirection.LeftToRight),
                new ScriptRun(2, 2, Arab, ShapeDirection.RightToLeft),
            ]);

    [Fact]
    public void SpacesAndPunctuation_AttachToPrecedingRun()
        => ScriptItemizer.Itemize("a b.c").ShouldBe([new ScriptRun(0, 5, Latn, ShapeDirection.LeftToRight)]);

    [Fact]
    public void CombiningMark_InheritsBaseRun()
        => ScriptItemizer.Itemize("á") // 'a' + combining acute (Inherited)
            .ShouldBe([new ScriptRun(0, 2, Latn, ShapeDirection.LeftToRight)]);

    [Fact]
    public void CommonBetweenScripts_AttachesToPreceding()
        // "عرب.pdf": the '.' (pos 3) attaches to the Arabic run, not the Latin one.
        => ScriptItemizer.Itemize("عرب.pdf").ShouldBe([
            new ScriptRun(0, 4, Arab, ShapeDirection.RightToLeft),
            new ScriptRun(4, 3, Latn, ShapeDirection.LeftToRight),
        ]);

    [Fact]
    public void LeadingCommon_AttachesForwardToFirstScript()
        // "123عرب": the leading digits fold into the Arabic run (level-B forward attach).
        => ScriptItemizer.Itemize("123عرب")
            .ShouldBe([new ScriptRun(0, 6, Arab, ShapeDirection.RightToLeft)]);

    [Fact]
    public void AllCommon_ResolvesToDefaultLtr()
        => ScriptItemizer.Itemize("123.-").ShouldBe([new ScriptRun(0, 5, Latn, ShapeDirection.LeftToRight)]);
}
