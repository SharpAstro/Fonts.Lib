using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Full rasterization pipeline: outline decode → flatten → SmoothRasterizer.
/// Parameterized by pixel size to show scaling behavior.
/// </summary>
[MemoryDiagnoser]
public class RasterizationBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private OpenTypeFont _sourceSans = null!;
    private uint _ttGlyphA;
    private uint _ttGlyphG;
    private uint _cffGlyphA;
    private uint _cffGlyphG;

    [Params(16, 48, 96)]
    public float PixelsPerEm { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _sourceSans = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));

        _ttGlyphA = _dejaVu.GetGlyphId('A');
        _ttGlyphG = _dejaVu.GetGlyphId('g');
        _cffGlyphA = _sourceSans.GetGlyphId('A');
        _cffGlyphG = _sourceSans.GetGlyphId('g');
    }

    [Benchmark(Description = "RenderGlyph 'A' TrueType")]
    public GlyphBitmap RenderTT_A() => _dejaVu.RenderGlyph(_ttGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderGlyph 'g' TrueType")]
    public GlyphBitmap RenderTT_g() => _dejaVu.RenderGlyph(_ttGlyphG, PixelsPerEm);

    [Benchmark(Description = "RenderGlyph 'A' CFF")]
    public GlyphBitmap RenderCff_A() => _sourceSans.RenderGlyph(_cffGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderGlyph 'g' CFF")]
    public GlyphBitmap RenderCff_g() => _sourceSans.RenderGlyph(_cffGlyphG, PixelsPerEm);
}
