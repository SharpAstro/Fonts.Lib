using BenchmarkDotNet.Attributes;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Variable font operations: axis instantiation (WithVariation) and
/// gvar-delta outline loading compared to the default instance.
/// </summary>
[MemoryDiagnoser]
public class VariationBenchmarks
{
    private OpenTypeFont _robotoDefault = null!;
    private OpenTypeFont _robotoBold = null!;
    private byte[] _robotoBytes = null!;
    private uint _glyphA;
    private uint _glyphG;

    private static readonly Dictionary<string, float> BoldCoords = new()
    {
        ["wght"] = 700f,
    };

    private static readonly Dictionary<string, float> FullCoords = new()
    {
        ["wght"] = 700f,
        ["wdth"] = 75f,
        ["opsz"] = 144f,
    };

    [GlobalSetup]
    public void Setup()
    {
        _robotoBytes = File.ReadAllBytes(Fixtures.Path(Fixtures.RobotoFlex));
        _robotoDefault = OpenTypeFont.Load(_robotoBytes);
        _robotoBold = _robotoDefault.WithVariation(BoldCoords);

        _glyphA = _robotoDefault.GetGlyphId('A');
        _glyphG = _robotoDefault.GetGlyphId('g');
    }

    [Benchmark(Description = "WithVariation (1 axis: wght=700)")]
    public OpenTypeFont InstantiateSingleAxis() => _robotoDefault.WithVariation(BoldCoords);

    [Benchmark(Description = "WithVariation (3 axes: wght+wdth+opsz)")]
    public OpenTypeFont InstantiateMultiAxis() => _robotoDefault.WithVariation(FullCoords);

    [Benchmark(Description = "LoadGlyphOutline 'A' default instance")]
    public Outlines.Outline OutlineDefault_A() => _robotoDefault.LoadGlyphOutline(_glyphA);

    [Benchmark(Description = "LoadGlyphOutline 'A' bold (gvar delta)")]
    public Outlines.Outline OutlineBold_A() => _robotoBold.LoadGlyphOutline(_glyphA);

    [Benchmark(Description = "LoadGlyphOutline 'g' default instance")]
    public Outlines.Outline OutlineDefault_g() => _robotoDefault.LoadGlyphOutline(_glyphG);

    [Benchmark(Description = "LoadGlyphOutline 'g' bold (gvar delta)")]
    public Outlines.Outline OutlineBold_g() => _robotoBold.LoadGlyphOutline(_glyphG);

    [Benchmark(Description = "RenderGlyph 'A' bold @48px (variation + raster)")]
    public Rasterizer.GlyphBitmap RenderBold_A() => _robotoBold.RenderGlyph(_glyphA, 48f);
}
