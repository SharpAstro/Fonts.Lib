namespace SharpAstro.Fonts.Benchmarks;

/// <summary>Centralized paths to fixture fonts (mirrors the test project).</summary>
internal static class Fixtures
{
    private static readonly string Root =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Path(string name) => System.IO.Path.Combine(Root, name);

    public const string DejaVuSans   = "DejaVuSans.ttf";
    public const string SourceSans3  = "SourceSans3-Regular.otf";
    public const string RobotoFlex   = "RobotoFlex.ttf";
    public const string NotoSansJP   = "NotoSansJP-Regular.otf";
    public const string Noto_COLRv1  = "Noto-COLRv1.ttf";
    public const string NotoColorEmoji = "NotoColorEmoji.ttf";
    public const string Merida       = "Merida.ttf";
}
