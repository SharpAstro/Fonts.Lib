using System.Globalization;
using System.Text;

namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Arabic joining type (UAX #9 / ArabicShaping.txt). Determines how a cursive-script letter
/// connects to its neighbours, which the Arabic shaper turns into the positional-form features
/// (<c>init</c>/<c>medi</c>/<c>fina</c>/<c>isol</c>). The byte values match the generated table
/// in <c>Joining.g.cs</c> — do not reorder without regenerating.
/// </summary>
internal enum JoiningType : byte
{
    /// <summary>Non_Joining (U) — does not connect on either side; breaks a join run.</summary>
    NonJoining = 0,
    /// <summary>Transparent (T) — combining marks and format controls; skipped when computing
    /// a neighbour's join and never assigned a positional form of their own.</summary>
    Transparent = 1,
    /// <summary>Dual_Joining (D) — connects on both sides.</summary>
    DualJoining = 2,
    /// <summary>Right_Joining (R) — connects only to the preceding letter (its right, visually).</summary>
    RightJoining = 3,
    /// <summary>Left_Joining (L) — connects only to the following letter (its left, visually).</summary>
    LeftJoining = 4,
    /// <summary>Join_Causing (C) — tatweel / ZWJ; causes joining on both sides like Dual_Joining.</summary>
    JoinCausing = 5,
}

internal static partial class Joining
{
    private const byte NotListed = 0xFF;

    /// <summary>
    /// The joining type of <paramref name="codepoint"/>. Codepoints listed in ArabicShaping.txt
    /// use their explicit type (including the signs explicitly marked Non_Joining that would
    /// otherwise default to Transparent). Anything unlisted follows the file's documented
    /// default: general category Mn/Me/Cf ⇒ Transparent, everything else ⇒ Non_Joining.
    /// </summary>
    public static JoiningType Get(uint codepoint)
    {
        var explicitType = UcdTables.RangeByte(Ranges, codepoint, NotListed);
        if (explicitType != NotListed)
            return (JoiningType)explicitType;

        return IsTransparentByCategory(codepoint) ? JoiningType.Transparent : JoiningType.NonJoining;
    }

    private static bool IsTransparentByCategory(uint codepoint)
        => Rune.TryCreate(codepoint, out var rune)
           && Rune.GetUnicodeCategory(rune) is
              UnicodeCategory.NonSpacingMark or
              UnicodeCategory.EnclosingMark or
              UnicodeCategory.Format;
}
