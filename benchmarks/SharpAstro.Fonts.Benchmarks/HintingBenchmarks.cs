using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Compares hinted vs. unhinted rendering to quantify the overhead
/// of the TrueType hinting pipeline (interpreter + CVT).
/// </summary>
[MemoryDiagnoser]
public class HintingBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private uint _glyphA;
    private uint _glyphG;

    [Params(12, 16, 48)]
    public float PixelsPerEm { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _glyphA = _dejaVu.GetGlyphId('A');
        _glyphG = _dejaVu.GetGlyphId('g');
    }

    [Benchmark(Baseline = true, Description = "RenderGlyph 'A' (unhinted)")]
    public GlyphBitmap Unhinted_A() => _dejaVu.RenderGlyph(_glyphA, PixelsPerEm);

    [Benchmark(Description = "RenderGlyphHinted 'A'")]
    public GlyphBitmap Hinted_A() => _dejaVu.RenderGlyphHinted(_glyphA, PixelsPerEm);

    [Benchmark(Description = "RenderGlyph 'g' (unhinted)")]
    public GlyphBitmap Unhinted_g() => _dejaVu.RenderGlyph(_glyphG, PixelsPerEm);

    [Benchmark(Description = "RenderGlyphHinted 'g'")]
    public GlyphBitmap Hinted_g() => _dejaVu.RenderGlyphHinted(_glyphG, PixelsPerEm);
}
