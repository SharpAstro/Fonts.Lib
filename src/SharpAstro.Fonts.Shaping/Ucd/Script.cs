using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Unicode Script property (Scripts.txt) as the OpenType script tag a run shapes under —
/// the lowercased ISO 15924 code (arab, latn, hebr, grek, cyrl, …). Drives script itemization.
/// Combining marks resolve to <see cref="Inherited"/> and neutral punctuation/digits/spaces to
/// <see cref="Common"/>; both attach to a neighbouring run rather than forming their own.
/// </summary>
internal static partial class Script
{
    /// <summary>Common (Zyyy) — punctuation, digits, spaces, symbols; attaches to a real run.</summary>
    public static readonly Tag Common = new("zyyy");

    /// <summary>Inherited (Zinh) — combining marks; takes the preceding base's script.</summary>
    public static readonly Tag Inherited = new("zinh");

    /// <summary>The OpenType script tag of <paramref name="codepoint"/>. Codepoints not covered by
    /// Scripts.txt (unassigned) resolve to <see cref="Common"/> so they attach to a neighbour.</summary>
    public static Tag Get(uint codepoint) => new(UcdTables.RangeU32(Ranges, codepoint, Common.Value));

    /// <summary>Whether <paramref name="script"/> lays out right-to-left. A fixed level-B set of the
    /// UI-relevant RTL scripts; full UAX #9 direction resolution is H6.</summary>
    public static bool IsRightToLeft(Tag script)
        => script == Arab || script == Hebr || script == Syrc || script == Thaa || script == Nkoo;

    private static readonly Tag Arab = new("arab");
    private static readonly Tag Hebr = new("hebr");
    private static readonly Tag Syrc = new("syrc");
    private static readonly Tag Thaa = new("thaa");
    // Stored form is the lowercased ISO code; the OT exception "nko " (trailing space) is not
    // applied — N'Ko isn't specially shaped, so it falls back to DFLT anyway.
    private static readonly Tag Nkoo = new("nkoo");
}
