namespace SharpAstro.Fonts.Tables.Cmap;

/// <summary>
/// Hints how to map a PDF char-code to a font glyph index. PDF embedded fonts
/// use a variety of encoding strategies; this enum picks the cmap-lookup
/// order that fits the font in question.
///
/// <para>Ported from DIR.Lib's <c>GlyphMapHint</c> — same semantics so calling
/// code can swap with no behavior change.</para>
/// </summary>
public enum GlyphMapHint
{
    /// <summary>Try every strategy: Unicode → MS Symbol PUA → Mac Roman → charCode-via-Unicode → direct GID.</summary>
    Auto = 0,

    /// <summary>
    /// Embedded PDF subset font. Tries Unicode → MS Symbol (PUA offset U+F000+code,
    /// then the raw code — mPDF CJK subsets key their (3,0) subtable by the raw 1-byte
    /// code) → direct GID. Skips Mac Roman (1,0), which maps charCodes to wrong GIDs in
    /// some subset fonts (Tahoma/ISOCPEUR).
    /// </summary>
    EmbeddedSubset,

    /// <summary>
    /// Identity CIDToGIDMap (or custom subset encoding) — the charCode IS
    /// the glyph index. Skips all cmap lookups.
    /// </summary>
    CharCodeIsGID,

    /// <summary>
    /// Standard-encoded font (WinAnsi / MacRoman) — Unicode cmap is
    /// reliable. Falls back to charCode-via-Unicode-cmap.
    /// </summary>
    Unicode,
}
