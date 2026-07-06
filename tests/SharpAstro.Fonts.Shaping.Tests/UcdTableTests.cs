using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// The generated UCD property tables (<c>tools/UcdGen</c>, UCD 17.0.0). These guard the packed
/// RVA blobs and their binary-search accessors: a bad regeneration or an off-by-one search
/// bound would surface as a wrong property value. The end-to-end joining/mirroring behaviour is
/// proven by the HarfBuzz conformance fixtures and <see cref="RtlMirroringTests"/>; this pins
/// the raw data those depend on.
/// </summary>
public class UcdTableTests
{
    // JoiningType is internal, so it can't sit in a public xunit signature; pass the value as
    // int and compare in the body.
    [Theory]
    [InlineData(0x0628u, (int)JoiningType.DualJoining)]   // beh
    [InlineData(0x0645u, (int)JoiningType.DualJoining)]   // meem
    [InlineData(0x0627u, (int)JoiningType.RightJoining)]  // alef
    [InlineData(0x0640u, (int)JoiningType.JoinCausing)]   // tatweel
    [InlineData(0x200Du, (int)JoiningType.JoinCausing)]   // ZWJ
    [InlineData(0x200Cu, (int)JoiningType.NonJoining)]    // ZWNJ — explicitly U
    [InlineData(0x0600u, (int)JoiningType.NonJoining)]    // Arabic number sign — explicit U overrides the Cf⇒T default
    [InlineData(0x0301u, (int)JoiningType.Transparent)]   // Latin combining acute — unlisted Mn ⇒ Transparent (fallback)
    [InlineData(0x0041u, (int)JoiningType.NonJoining)]    // 'A' — unlisted, not a mark ⇒ Non_Joining (fallback)
    public void Joining_Get(uint codepoint, int expected)
        => ((int)Joining.Get(codepoint)).ShouldBe(expected);

    // Canonical combining class beyond the Latin/Greek block H2 hand-transcribed — proves the
    // table is now UCD-wide (any nonzero-CCC codepoint resolves), while the Latin block and
    // starters still resolve correctly.
    [Theory]
    [InlineData(0x05B0u, 10)]   // Hebrew point sheva
    [InlineData(0x064Bu, 27)]   // Arabic fathatan
    [InlineData(0x0651u, 33)]   // Arabic shadda
    [InlineData(0x0670u, 35)]   // Arabic letter superscript alef
    [InlineData(0x3099u, 8)]    // combining katakana-hiragana voiced sound mark
    [InlineData(0x0E38u, 103)]  // Thai character sara u
    [InlineData(0x0301u, 230)]  // combining acute — still correct in the Latin block
    [InlineData(0x0041u, 0)]    // 'A' — a starter
    public void Ccc_Get(uint codepoint, int expected)
        => CanonicalCombiningClass.Get(codepoint).ShouldBe((byte)expected);

    [Theory]
    [InlineData(0x0028u, 0x0029u)] // ( ⇄ )
    [InlineData(0x0029u, 0x0028u)]
    [InlineData(0x003Cu, 0x003Eu)] // < ⇄ >
    [InlineData(0x005Bu, 0x005Du)] // [ ⇄ ]
    [InlineData(0x007Bu, 0x007Du)] // { ⇄ }
    [InlineData(0x00ABu, 0x00BBu)] // « ⇄ »
    public void Mirror_Get(uint codepoint, uint expected)
        => BidiMirroring.Get(codepoint).ShouldBe(expected);

    [Theory]
    [InlineData(0x0041u)] // 'A' — no mirror
    [InlineData(0x0628u)] // Arabic beh — no mirror
    public void Mirror_LeavesUnmirroredUnchanged(uint codepoint)
        => BidiMirroring.Get(codepoint).ShouldBe(codepoint);

    // Script.Get dispatches through the generated page index (cp>>8) into the range table. Cover
    // several scripts, a page boundary (U+02FF Latin | U+0300 Inherited), and a codepoint whose
    // page is beyond the table — all resolving as the pre-paged binary search did.
    [Theory]
    [InlineData(0x0041u, "latn")]  // 'A'
    [InlineData(0x0391u, "grek")]  // Greek capital alpha
    [InlineData(0x05D0u, "hebr")]  // Hebrew alef
    [InlineData(0x0628u, "arab")]  // Arabic beh
    [InlineData(0x4E00u, "hani")]  // CJK unified ideograph
    [InlineData(0x0300u, "zinh")]  // combining grave — Inherited, and the page after U+02FF
    [InlineData(0xF0000u, "zyyy")] // plane-15 unassigned — page beyond the table ⇒ Common
    public void Script_Get(uint codepoint, string expectedTag)
        => Script.Get(codepoint).ShouldBe(new Tag(expectedTag));

    // Bidi_Class: strong (L/R/AL), weak (EN/AN/ET), neutral (WS/ON), an isolate (LRI), and an
    // @missing default — U+05FF is unassigned but sits in the Hebrew block, so it resolves to R
    // (not the global L default). That default is exactly why the table is built from
    // DerivedBidiClass.txt rather than UnicodeData.txt field 4. BidiClass is internal, so pass int.
    [Theory]
    [InlineData(0x0041u, (int)BidiClass.L)]    // 'A'
    [InlineData(0x05D0u, (int)BidiClass.R)]    // Hebrew alef
    [InlineData(0x0627u, (int)BidiClass.AL)]   // Arabic alef
    [InlineData(0x0030u, (int)BidiClass.EN)]   // '0'
    [InlineData(0x0660u, (int)BidiClass.AN)]   // Arabic-Indic digit zero
    [InlineData(0x0024u, (int)BidiClass.ET)]   // '$'
    [InlineData(0x0020u, (int)BidiClass.WS)]   // space
    [InlineData(0x0028u, (int)BidiClass.ON)]   // '('
    [InlineData(0x2066u, (int)BidiClass.LRI)]  // LEFT-TO-RIGHT ISOLATE
    [InlineData(0x05FFu, (int)BidiClass.R)]    // unassigned in the Hebrew block -> @missing R
    public void BidiClass_Get(uint codepoint, int expected)
        => ((int)Bidi.Get(codepoint)).ShouldBe(expected);

    [Theory]
    [InlineData(0x0028u, 0x0029u, true)]   // ( opens, pairs with )
    [InlineData(0x0029u, 0x0028u, false)]  // ) closes, pairs with (
    [InlineData(0x005Bu, 0x005Du, true)]   // [ opens, pairs with ]
    public void BidiBrackets_TryGet(uint codepoint, uint expectedPair, bool expectedOpen)
    {
        BidiBrackets.TryGet(codepoint, out var paired, out var isOpen).ShouldBeTrue();
        paired.ShouldBe(expectedPair);
        isOpen.ShouldBe(expectedOpen);
    }

    [Fact]
    public void BidiBrackets_TryGet_NonBracket_ReturnsFalse()
        => BidiBrackets.TryGet(0x0041u, out _, out _).ShouldBeFalse();
}
