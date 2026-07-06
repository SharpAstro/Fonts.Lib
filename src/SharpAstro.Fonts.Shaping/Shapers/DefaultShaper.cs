using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The default shaper for non-cursive scripts — Latin, Greek, Cyrillic, CJK, and Hebrew. It
/// runs the script-independent feature set and relies on <see cref="ShaperBase"/> for RTL
/// mirroring + reversal, so Hebrew (RTL, non-joining) needs nothing beyond the base. No
/// per-glyph masks: every feature applies to the whole run.
/// </summary>
internal sealed class DefaultShaper : ShaperBase
{
    // rlig is only meaningful once a shaper assigns joining masks but is harmless to enable
    // generally (HarfBuzz enables it by default too).
    internal override Tag[] GsubFeatures { get; } =
        [new("ccmp"), new("liga"), new("clig"), new("calt"), new("rlig")];

    internal override Tag[] GposFeatures { get; } =
        [new("kern"), new("mark"), new("mkmk")];
}
