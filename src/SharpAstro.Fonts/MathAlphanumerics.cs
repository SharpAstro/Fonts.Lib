namespace SharpAstro.Fonts;

/// <summary>
/// Visual style for a math glyph variant. Maps onto MathML's
/// <c>mathvariant</c> attribute and onto the Unicode "Mathematical
/// Alphanumeric Symbols" block (U+1D400–U+1D7FF) plus the letter-like
/// symbols at U+2100–U+214F.
/// </summary>
public enum MathStyle
{
    /// <summary>Plain — no remapping; the codepoint is used as-is.</summary>
    Normal,
    Bold,
    Italic,
    BoldItalic,
    Script,
    BoldScript,
    Fraktur,
    BoldFraktur,
    DoubleStruck,
    SansSerif,
    SansSerifBold,
    SansSerifItalic,
    SansSerifBoldItalic,
    Monospace,
}

/// <summary>
/// Pure-Unicode mapping from a base character (Latin letter, Greek
/// letter, digit) plus a <see cref="MathStyle"/> to the codepoint of
/// its styled variant in the "Mathematical Alphanumeric Symbols" block
/// (U+1D400–U+1D7FF). Holes in that block — a handful of letters that
/// were already encoded in the Letterlike Symbols block (U+2100–U+214F)
/// when U+1D400 was added — are routed to those earlier codepoints, so
/// e.g. italic <c>h</c> resolves to U+210E (PLANCK CONSTANT) rather
/// than the unassigned U+1D455.
///
/// <para>This class only does the Unicode lookup — it does not touch
/// any font. To find the actual glyph, pass the result to a font's
/// cmap (see <see cref="OpenTypeFont.GetMathVariantGlyphId"/>). A
/// returned codepoint that the font doesn't cover gives glyph id 0,
/// which the caller treats as "no styled variant available — fall
/// back to the original codepoint's glyph".</para>
///
/// <para>Spec: <see href="https://www.unicode.org/charts/PDF/U1D400.pdf"/></para>
/// </summary>
public static class MathAlphanumerics
{
    /// <summary>
    /// Map (<paramref name="codepoint"/>, <paramref name="style"/>) to
    /// the styled-variant codepoint, or null if the combination has no
    /// Unicode mapping (e.g. italic digits, Fraktur Greek, monospace
    /// Greek). Returns <paramref name="codepoint"/> unchanged for
    /// <see cref="MathStyle.Normal"/>.
    /// </summary>
    public static uint? MapCodepoint(uint codepoint, MathStyle style)
    {
        if (style == MathStyle.Normal) return codepoint;

        // Latin uppercase A-Z.
        if (codepoint >= 'A' && codepoint <= 'Z')
        {
            var off = codepoint - (uint)'A';
            uint baseStart = style switch
            {
                MathStyle.Bold                  => 0x1D400,
                MathStyle.Italic                => 0x1D434,
                MathStyle.BoldItalic            => 0x1D468,
                MathStyle.Script                => 0x1D49C,
                MathStyle.BoldScript            => 0x1D4D0,
                MathStyle.Fraktur               => 0x1D504,
                MathStyle.DoubleStruck          => 0x1D538,
                MathStyle.BoldFraktur           => 0x1D56C,
                MathStyle.SansSerif             => 0x1D5A0,
                MathStyle.SansSerifBold         => 0x1D5D4,
                MathStyle.SansSerifItalic       => 0x1D608,
                MathStyle.SansSerifBoldItalic   => 0x1D63C,
                MathStyle.Monospace             => 0x1D670,
                _ => 0,
            };
            if (baseStart == 0) return null;
            return ResolveHole(baseStart + off);
        }

        // Latin lowercase a-z.
        if (codepoint >= 'a' && codepoint <= 'z')
        {
            var off = codepoint - (uint)'a';
            uint baseStart = style switch
            {
                MathStyle.Bold                  => 0x1D41A,
                MathStyle.Italic                => 0x1D44E,
                MathStyle.BoldItalic            => 0x1D482,
                MathStyle.Script                => 0x1D4B6,
                MathStyle.BoldScript            => 0x1D4EA,
                MathStyle.Fraktur               => 0x1D51E,
                MathStyle.DoubleStruck          => 0x1D552,
                MathStyle.BoldFraktur           => 0x1D586,
                MathStyle.SansSerif             => 0x1D5BA,
                MathStyle.SansSerifBold         => 0x1D5EE,
                MathStyle.SansSerifItalic       => 0x1D622,
                MathStyle.SansSerifBoldItalic   => 0x1D656,
                MathStyle.Monospace             => 0x1D68A,
                _ => 0,
            };
            if (baseStart == 0) return null;
            return ResolveHole(baseStart + off);
        }

        // Greek uppercase Α-Ω (U+0391-U+03A9). U+03A2 is reserved/skipped
        // by Unicode in the math block (it's a hole — there's no math
        // variant for the missing letter slot), but we don't need to
        // special-case it because the source codepoint U+03A2 itself
        // isn't a valid letter, so callers can't pass it.
        if (codepoint >= 0x0391 && codepoint <= 0x03A9)
        {
            var off = codepoint - 0x0391u;
            uint baseStart = style switch
            {
                MathStyle.Bold                  => 0x1D6A8,
                MathStyle.Italic                => 0x1D6E2,
                MathStyle.BoldItalic            => 0x1D71C,
                MathStyle.SansSerifBold         => 0x1D756,
                MathStyle.SansSerifBoldItalic   => 0x1D790,
                _ => 0,
            };
            if (baseStart == 0) return null;
            return baseStart + off;
        }

        // Greek lowercase α-ω (U+03B1-U+03C9).
        if (codepoint >= 0x03B1 && codepoint <= 0x03C9)
        {
            var off = codepoint - 0x03B1u;
            uint baseStart = style switch
            {
                MathStyle.Bold                  => 0x1D6C2,
                MathStyle.Italic                => 0x1D6FC,
                MathStyle.BoldItalic            => 0x1D736,
                MathStyle.SansSerifBold         => 0x1D770,
                MathStyle.SansSerifBoldItalic   => 0x1D7AA,
                _ => 0,
            };
            if (baseStart == 0) return null;
            return baseStart + off;
        }

        // Digits 0-9. No italic style for digits — only upright forms.
        if (codepoint >= '0' && codepoint <= '9')
        {
            var off = codepoint - (uint)'0';
            uint baseStart = style switch
            {
                MathStyle.Bold                  => 0x1D7CE,
                MathStyle.DoubleStruck          => 0x1D7D8,
                MathStyle.SansSerif             => 0x1D7E2,
                MathStyle.SansSerifBold         => 0x1D7EC,
                MathStyle.Monospace             => 0x1D7F6,
                _ => 0,
            };
            if (baseStart == 0) return null;
            return baseStart + off;
        }

        return null;
    }

    /// <summary>
    /// The U+1D400 block has fourteen reserved codepoints corresponding
    /// to letters that were already encoded in the Letterlike Symbols
    /// block (U+2100–U+214F) before the math alphanumerics were added.
    /// Map those holes to their canonical Letterlike-Symbols codepoints.
    /// </summary>
    private static uint ResolveHole(uint codepoint) => codepoint switch
    {
        // Italic
        0x1D455 => 0x210E, // ITALIC SMALL H → PLANCK CONSTANT

        // Script (uppercase)
        0x1D49D => 0x212C, // SCRIPT B
        0x1D4A0 => 0x2130, // SCRIPT E
        0x1D4A1 => 0x2131, // SCRIPT F
        0x1D4A3 => 0x210B, // SCRIPT H
        0x1D4A4 => 0x2110, // SCRIPT I
        0x1D4A7 => 0x2112, // SCRIPT L
        0x1D4A8 => 0x2133, // SCRIPT M
        0x1D4AD => 0x211B, // SCRIPT R

        // Script (lowercase)
        0x1D4BA => 0x212F, // SCRIPT e
        0x1D4BC => 0x210A, // SCRIPT g
        0x1D4C4 => 0x2134, // SCRIPT o

        // Fraktur (uppercase)
        0x1D506 => 0x212D, // FRAKTUR C
        0x1D50B => 0x210C, // FRAKTUR H
        0x1D50C => 0x2111, // FRAKTUR I
        0x1D515 => 0x211C, // FRAKTUR R
        0x1D51D => 0x2128, // FRAKTUR Z

        // Double-struck (uppercase)
        0x1D53A => 0x2102, // DOUBLE-STRUCK C
        0x1D53F => 0x210D, // DOUBLE-STRUCK H
        0x1D545 => 0x2115, // DOUBLE-STRUCK N
        0x1D547 => 0x2119, // DOUBLE-STRUCK P
        0x1D548 => 0x211A, // DOUBLE-STRUCK Q
        0x1D549 => 0x211D, // DOUBLE-STRUCK R
        0x1D551 => 0x2124, // DOUBLE-STRUCK Z

        _ => codepoint,
    };
}
