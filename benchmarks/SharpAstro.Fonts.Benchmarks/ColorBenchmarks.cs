using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Color;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Color glyph rendering: COLR v0/v1 paint-tree walk + rasterize,
/// and CBDT PNG bitmap decode.
/// </summary>
[MemoryDiagnoser]
public class ColorBenchmarks
{
    private OpenTypeFont _colrv1 = null!;
    private OpenTypeFont _cbdt = null!;
    private uint _colrGlyph;
    private uint _cbdtGlyph;

    [GlobalSetup]
    public void Setup()
    {
        _colrv1 = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        _cbdt   = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoColorEmoji));

        // U+1F600 Grinning Face — present in both COLR and CBDT emoji fonts.
        _colrGlyph = _colrv1.GetGlyphId(0x1F600);
        _cbdtGlyph = _cbdt.GetGlyphId(0x1F600);
    }

    [Benchmark(Description = "RenderColor COLR v1 U+1F600 @32px")]
    public ColorBitmap? ColrV1_32() => _colrv1.RenderColor(_colrGlyph, 32f);

    [Benchmark(Description = "RenderColor COLR v1 U+1F600 @128px")]
    public ColorBitmap? ColrV1_128() => _colrv1.RenderColor(_colrGlyph, 128f);

    [Benchmark(Description = "RenderColor CBDT U+1F600 @32px")]
    public ColorBitmap? Cbdt_32() => _cbdt.RenderColor(_cbdtGlyph, 32f);

    [Benchmark(Description = "RenderColor CBDT U+1F600 @128px")]
    public ColorBitmap? Cbdt_128() => _cbdt.RenderColor(_cbdtGlyph, 128f);
}
