using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Measures glyph outline loading and path emission for both
/// TrueType (glyf) and CFF charstring paths.
/// </summary>
[MemoryDiagnoser]
public class OutlineBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private OpenTypeFont _sourceSans = null!;

    // Pre-resolved glyph IDs.
    private uint _ttGlyphA;
    private uint _ttGlyphG;
    private uint _cffGlyphA;
    private uint _cffGlyphG;

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _sourceSans = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));

        _ttGlyphA = _dejaVu.GetGlyphId('A');
        _ttGlyphG = _dejaVu.GetGlyphId('g');   // complex contours
        _cffGlyphA = _sourceSans.GetGlyphId('A');
        _cffGlyphG = _sourceSans.GetGlyphId('g');
    }

    [Benchmark(Description = "LoadGlyphOutline 'A' (TrueType)")]
    public Outline LoadOutlineTT_A() => _dejaVu.LoadGlyphOutline(_ttGlyphA);

    [Benchmark(Description = "LoadGlyphOutline 'g' (TrueType)")]
    public Outline LoadOutlineTT_g() => _dejaVu.LoadGlyphOutline(_ttGlyphG);

    [Benchmark(Description = "DrawGlyph 'A' (CFF charstring)")]
    public void DrawGlyphCff_A() => _sourceSans.DrawGlyph(_cffGlyphA, NullSink.Instance);

    [Benchmark(Description = "DrawGlyph 'g' (CFF charstring)")]
    public void DrawGlyphCff_g() => _sourceSans.DrawGlyph(_cffGlyphG, NullSink.Instance);

    [Benchmark(Description = "DrawGlyph 'A' (TrueType → flatten)")]
    public void DrawGlyphTT_A() => _dejaVu.DrawGlyph(_ttGlyphA, NullSink.Instance);

    /// <summary>A no-op sink that discards all path commands (measures pure decode cost).</summary>
    private sealed class NullSink : IGlyphSink
    {
        public static readonly NullSink Instance = new();
        public void MoveTo(float x, float y) { }
        public void LineTo(float x, float y) { }
        public void QuadTo(float cx, float cy, float x, float y) { }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) { }
        public void Close() { }
    }
}
