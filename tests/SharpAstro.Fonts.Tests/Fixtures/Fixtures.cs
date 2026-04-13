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
    public const string Merida                 = "Merida.ttf";
    public const string DejaVuSans             = "DejaVuSans.ttf";
    public const string Noto_COLRv1            = "Noto-COLRv1.ttf";
    public const string NotoColorEmoji         = "NotoColorEmoji.ttf";
    public const string BabelStoneXiangqiColour = "BabelStoneXiangqiColour.ttf";
    /// <summary>SourceSans3-Regular.otf — CFF/OTF reference (SIL OFL 1.1).</summary>
    public const string SourceSans3 = "SourceSans3-Regular.otf";

    /// <summary>All bundled fixture fonts. Useful for "applies-to-every-font" smoke tests.</summary>
    public static readonly string[] All =
    [
        XXTIIT_Arial_Subset, Tahoma_Subset, ISOCPEUR_Subset, Merida,
        DejaVuSans, Noto_COLRv1, NotoColorEmoji, BabelStoneXiangqiColour,
        SourceSans3,
    ];
}
