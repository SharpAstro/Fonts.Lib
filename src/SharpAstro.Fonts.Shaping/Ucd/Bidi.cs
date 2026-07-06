namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Unicode Bidi_Class (UAX #9) — the per-character type the bidirectional algorithm resolves
/// embedding levels from. The byte values are the on-disk encoding shared with <c>tools/UcdGen</c>
/// and the generated <see cref="Bidi.Ranges"/> table; do not renumber without regenerating.
/// </summary>
internal enum BidiClass : byte
{
    // Strong
    L = 0, R = 1, AL = 2,
    // Weak
    EN = 3, ES = 4, ET = 5, AN = 6, CS = 7, NSM = 8, BN = 9,
    // Neutral
    B = 10, S = 11, WS = 12, ON = 13,
    // Explicit formatting
    LRE = 14, LRO = 15, RLE = 16, RLO = 17, PDF = 18,
    // Isolates
    LRI = 19, RLI = 20, FSI = 21, PDI = 22,
}

/// <summary>Bidi_Class lookup over the generated UCD range table.</summary>
internal static partial class Bidi
{
    /// <summary>The Bidi_Class of <paramref name="codepoint"/>. Codepoints not in the table are
    /// <see cref="BidiClass.L"/> — the global <c>@missing</c> default. Only the non-L ranges are
    /// stored, so plain Latin/ASCII resolves without a table hit across most of the plane.</summary>
    public static BidiClass Get(uint codepoint) => (BidiClass)UcdTables.RangeByte(Ranges, codepoint, notFound: 0);
}
