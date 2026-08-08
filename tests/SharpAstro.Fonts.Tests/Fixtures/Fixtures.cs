namespace SharpAstro.Fonts.Tests;

/// <summary>Centralized paths to test fixture fonts.</summary>
internal static class Fixtures
{
    private static readonly string Root =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Path(string name) => System.IO.Path.Combine(Root, name);

    public const string XXTIIT_Arial_Subset    = "XXTIIT_Arial_subset.ttf";
    public const string Tahoma_Subset          = "Tahoma_subset.ttf";
    public const string ISOCPEUR_Subset        = "ISOCPEUR_subset.ttf";
    /// <summary>Canon EOS450D manual's D011A key-cap subset (HJIPGJ+D011A): its (3,1) fmt4
    /// cmap declares a length 6 bytes past the physical cmap table end. Regression fixture for
    /// tolerating overstated subtable lengths instead of rejecting the font.</summary>
    public const string D011A_Subset           = "D011A_subset.ttf";
    /// <summary>Canon EOS450D manual's Lithos-Bold subset: a bare name-keyed CFF (PDF
    /// /FontFile3) with NO Encoding operator — glyph selection is only possible by name
    /// through the charset. Regression fixture for GetGlyphIdByName.</summary>
    public const string LithosBold_Subset      = "LithosBold_subset.cff";
    public const string Merida                 = "Merida.ttf";
    public const string DejaVuSans             = "DejaVuSans.ttf";
    public const string Noto_COLRv1            = "Noto-COLRv1.ttf";
    public const string NotoColorEmoji         = "NotoColorEmoji.ttf";
    public const string BabelStoneXiangqiColour = "BabelStoneXiangqiColour.ttf";
    /// <summary>SourceSans3-Regular.otf — CFF/OTF reference (SIL OFL 1.1).</summary>
    public const string SourceSans3 = "SourceSans3-Regular.otf";
    /// <summary>RobotoFlex.ttf — variable font reference (SIL OFL 1.1).</summary>
    public const string RobotoFlex = "RobotoFlex.ttf";
    /// <summary>NotoSansJP-Regular.otf — CJK CFF/OTF with cmap format 14 (IVS). SIL OFL 1.1.</summary>
    public const string NotoSansJP = "NotoSansJP-Regular.otf";
    /// <summary>NotoSansKR-Regular.otf — CJK CFF/OTF with cmap format 14 (IVS). SIL OFL 1.1.</summary>
    public const string NotoSansKR = "NotoSansKR-Regular.otf";
    /// <summary>NotoSansSC-Regular.otf — CJK CFF/OTF with cmap format 14 (IVS). SIL OFL 1.1.</summary>
    public const string NotoSansSC = "NotoSansSC-Regular.otf";
    /// <summary>NotoSansTC-Regular.otf — CJK CFF/OTF with cmap format 14 (IVS). SIL OFL 1.1.</summary>
    public const string NotoSansTC = "NotoSansTC-Regular.otf";
    /// <summary>NotoSans-Regular.ttf (hinted build, SIL OFL 1.1) — the face that exposed the
    /// three compounding hinting-interpreter defects (dead twilight zone, ALIGNRP not draining
    /// its operands, <c>cvt </c> read unsigned). It is the only bundled face with negative
    /// control values — 26 of 150 — which is precisely why DejaVuSans looked clean throughout
    /// and this one hung the process on <c>g</c> and <c>x</c>. Keep it as the hinting regression
    /// fixture; see <c>HintingCorrectnessTests</c>.</summary>
    public const string NotoSans = "NotoSans-Regular.ttf";

    /// <summary>AutoCAD SHX <c>unifont</c> fixture — 7 glyphs (<c>I L A O Z T -</c>), authored from
    /// scratch by <c>tools/make_shx_fixtures.py</c>. Autodesk's stock faces are their IP and cannot
    /// be bundled, so these are our own bytes in their format; synthetic is also the stronger
    /// fixture, since every opcode is present deliberately and the geometry is known exactly.
    /// <para><c>O</c> is a full circle from four octant arcs (<c>0x0A</c>) — a decoder that skips
    /// arcs returns an empty glyph, and <c>txt.shx</c> cannot catch that because it contains no
    /// arcs at all. <c>I</c> has zero width and <c>-</c> zero height (the normalisation traps).
    /// <c>Z</c> carries a non-empty glyph name plus a vertical-mode command that must be skipped
    /// in horizontal text. <c>T</c> pulls <c>I</c> in by subshape reference (<c>0x07</c>) at code
    /// <c>0x0049</c>, whose operand is big-endian — read the other way it resolves to nothing and
    /// only the crossbar survives.</para></summary>
    public const string ShxTestUnifont = "SharpAstroTest-unifont.shx";

    /// <summary>AutoCAD SHX <c>bigfont</c> fixture — 3 double-byte glyphs at <c>0x8141</c>,
    /// <c>0x8142</c> and <c>0x8143</c>, one lead-byte range <c>0x81-0x81</c>. Authored from
    /// scratch; see <see cref="ShxTestUnifont"/> for why.
    /// <para>bigfont is a genuinely different container from unifont, not just a different header:
    /// records are reached through an index table of <c>(code, length, uint32 offset)</c> entries
    /// pointing into a contiguous data area, where unifont stores them inline. Reading one with the
    /// other's layout overruns EOF. <c>0x8143</c> composes <c>0x8142</c> through the extended
    /// subshape form (<c>0x07 0x00</c> plus a placement box), which is how CJK glyphs are built
    /// from radicals and which a plain 1-byte reading of <c>0x07</c> desynchronises.</para></summary>
    public const string ShxTestBigfont = "SharpAstroTest-bigfont.shx";

    /// <summary>All bundled fixture fonts. Useful for "applies-to-every-font" smoke tests.
    /// Excludes the SHX fixtures — those are not SFNT and do not load through
    /// <c>OpenTypeFont</c>.</summary>
    public static readonly string[] All =
    [
        XXTIIT_Arial_Subset, Tahoma_Subset, ISOCPEUR_Subset, D011A_Subset, Merida,
        DejaVuSans, Noto_COLRv1, NotoColorEmoji, BabelStoneXiangqiColour,
        SourceSans3, RobotoFlex,
        NotoSansJP, NotoSansKR, NotoSansSC, NotoSansTC,
    ];
}
