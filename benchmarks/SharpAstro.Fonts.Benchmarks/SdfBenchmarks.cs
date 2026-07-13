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
    private uint _cffGlyphO;

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
        _cffGlyphO = _sourceSans.GetGlyphId('o');
    }

    [Benchmark(Description = "RenderSdf 'A' TrueType")]
    public SdfBitmap SdfTT_A() => _dejaVu.RenderSdf(_ttGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderSdf 'g' TrueType")]
    public SdfBitmap SdfTT_g() => _dejaVu.RenderSdf(_ttGlyphG, PixelsPerEm);

    [Benchmark(Description = "RenderSdf 'A' CFF")]
    public SdfBitmap SdfCff_A() => _sourceSans.RenderSdf(_cffGlyphA, PixelsPerEm);

    // MTSDF is what the atlas renderer consumes; it layers edge coloring, per-channel
    // pseudo-distances, and the error-correction passes (winding queries + interpolation
    // sweep) on top of the base distance evaluation. 'g'/'o' are the joint-heavy round
    // glyphs that stress the y-monotone crossing counter.
    [Benchmark(Description = "RenderMtsdf 'A' TrueType")]
    public MtsdfBitmap MtsdfTT_A() => _dejaVu.RenderMtsdf(_ttGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderMtsdf 'g' TrueType")]
    public MtsdfBitmap MtsdfTT_g() => _dejaVu.RenderMtsdf(_ttGlyphG, PixelsPerEm);

    [Benchmark(Description = "RenderMtsdf 'A' CFF")]
    public MtsdfBitmap MtsdfCff_A() => _sourceSans.RenderMtsdf(_cffGlyphA, PixelsPerEm);

    [Benchmark(Description = "RenderMtsdf 'o' CFF")]
    public MtsdfBitmap MtsdfCff_o() => _sourceSans.RenderMtsdf(_cffGlyphO, PixelsPerEm);
}
