using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The shaper for cursive-joining Arabic. It analyses each letter's <see cref="JoiningType"/>
/// to select its positional form — <c>init</c>/<c>medi</c>/<c>fina</c>/<c>isol</c> — and enables
/// exactly that form's GSUB feature per glyph via the plan's per-glyph mask bits. Everything
/// else (ccmp, rlig, calt, liga; GPOS curs/kern/mark/mkmk) applies run-wide. RTL mirroring and
/// reversal come from <see cref="ShaperBase"/>.
///
/// <para>The joining state machine is the standard one: a connection forms between two adjacent
/// non-transparent letters when the earlier one joins on its left (L/D/C) and the later one
/// joins on its right (R/D/C). Combining marks are Transparent — skipped when finding neighbours
/// and never given a form. v1 scope is Arabic (<c>arab</c>); other cursive scripts stay on the
/// default shaper.</para>
/// </summary>
internal sealed class ArabicShaper : ShaperBase
{
    private static readonly Tag Isol = new("isol");
    private static readonly Tag Fina = new("fina");
    private static readonly Tag Medi = new("medi");
    private static readonly Tag Init = new("init");

    // Application order is by lookup index regardless of this list; the order only fixes which
    // features exist and their mask-bit numbering. Matches the plan's Arabic feature set.
    internal override Tag[] GsubFeatures { get; } =
        [new("ccmp"), Isol, Fina, Medi, Init, new("rlig"), new("calt"), new("liga")];

    internal override Tag[] GposFeatures { get; } =
        [new("curs"), new("kern"), new("mark"), new("mkmk")];

    internal override Tag[] PerGlyphFeatures { get; } = [Isol, Fina, Medi, Init];

    protected override void AssignMasks(ShapingFont font, ShapeBuffer buffer, ShapePlan plan)
    {
        _ = font;
        var isolBit = plan.GsubFeatureMask(Isol);
        var finaBit = plan.GsubFeatureMask(Fina);
        var mediBit = plan.GsubFeatureMask(Medi);
        var initBit = plan.GsubFeatureMask(Init);
        var formMask = (ushort)(isolBit | finaBit | mediBit | initBit);
        if (formMask == 0) return; // no positional-form features in the plan — nothing to select

        var cps = buffer.GlyphsMutable; // codepoints (before mapping)
        var masks = buffer.MasksMutable;

        var prevIndex = -1;      // last non-transparent slot
        var prevCanLeft = false; // whether it connects on its left (toward the current slot)
        var prevBit = (ushort)0; // the form bit currently set on prevIndex (isol or fina)

        for (var i = 0; i < cps.Length; i++)
        {
            masks[i] = (ushort)(masks[i] & ~formMask); // clear form bits; transparent slots stay cleared
            var joiningType = Joining.Get(cps[i]);
            if (joiningType == JoiningType.Transparent)
                continue;

            var canRight = joiningType is JoiningType.RightJoining or JoiningType.DualJoining or JoiningType.JoinCausing;
            var canLeft = joiningType is JoiningType.LeftJoining or JoiningType.DualJoining or JoiningType.JoinCausing;

            var joinsPrev = prevIndex >= 0 && prevCanLeft && canRight;
            var bit = joinsPrev ? finaBit : isolBit;
            masks[i] = (ushort)(masks[i] | bit);

            if (joinsPrev)
            {
                // prev now also connects on its left → isolated becomes initial, final becomes medial.
                var promoted = prevBit == isolBit ? initBit : mediBit;
                masks[prevIndex] = (ushort)((masks[prevIndex] & ~formMask) | promoted);
            }

            prevIndex = i;
            prevCanLeft = canLeft;
            prevBit = bit;
        }
    }
}
