namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Bidi_Paired_Bracket + Bidi_Paired_Bracket_Type (BidiBrackets.txt) — the bracket-pair data that
/// UAX #9 rule N0 uses to resolve the direction of paired brackets (parentheses, square/curly
/// brackets, …). The generated <see cref="Pairs"/> table maps a bracket codepoint to its canonical
/// paired codepoint (21 bits) with the opening-vs-closing flag packed into bit 23.
/// </summary>
internal static partial class BidiBrackets
{
    /// <summary>If <paramref name="codepoint"/> is a paired bracket, sets <paramref name="paired"/>
    /// to its canonical counterpart and <paramref name="isOpen"/> to whether it opens the pair, and
    /// returns true; otherwise returns false (and leaves the outputs at their defaults).</summary>
    public static bool TryGet(uint codepoint, out uint paired, out bool isOpen)
    {
        var packed = UcdTables.PairValue(Pairs, codepoint, uint.MaxValue);
        if (packed == uint.MaxValue)
        {
            paired = 0;
            isOpen = false;
            return false;
        }
        paired = packed & 0x1FFFFF;
        isOpen = (packed & 0x800000) != 0;
        return true;
    }
}
