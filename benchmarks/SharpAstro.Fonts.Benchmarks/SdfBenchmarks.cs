using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// SDF rasterization is O(width × height × edges) — the most expensive
/// single-glyph operation. Benchmarks at multiple sizes expose the
/// quadratic scaling and are the main optimization target.
/// </summary>
[MemoryDiagnoser]
public class SdfBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private OpenTypeFont _sourceSans = null!;
    private uint _ttGlyphA;
    private uint _ttGlyphG;
    private uint _cffGlyphA;

    [Params(16, 48)]
    public float PixelsPerEm { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _sourceSans = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));

        _ttGlyphA = _dejaVu.GetGlyphId('A');
        _ttGlyphG = _dejaVu.GetGlyphId('g');
        _cffGlyphA = _sourceSans.GetGlyphId('A');
    }

    [Benchmark(Description = "RenderSdf 'A' TrueType")]
    public SdfBitmap SdfTT_A() => _dejaVu.RenderSdf(_ttGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderSdf 'g' TrueType")]
    public SdfBitmap SdfTT_g() => _dejaVu.RenderSdf(_ttGlyphG, PixelsPerEm);

    [Benchmark(Description = "RenderSdf 'A' CFF")]
    public SdfBitmap SdfCff_A() => _sourceSans.RenderSdf(_cffGlyphA, PixelsPerEm);
}
