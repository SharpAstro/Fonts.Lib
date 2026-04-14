using BenchmarkDotNet.Attributes;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Kerning/pair-adjustment lookup via GPOS (format 1/2) and legacy kern table.
/// Simulates the inner loop of a text layout engine.
/// </summary>
[MemoryDiagnoser]
public class KerningBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private OpenTypeFont _sourceSans = null!;

    // Common kern pairs.
    private uint _ttA, _ttV, _ttT, _ttO, _ttW, _ttA2;
    private uint _cffA, _cffV, _cffT, _cffO;

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _sourceSans = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));

        _ttA  = _dejaVu.GetGlyphId('A');
        _ttV  = _dejaVu.GetGlyphId('V');
        _ttT  = _dejaVu.GetGlyphId('T');
        _ttO  = _dejaVu.GetGlyphId('o');
        _ttW  = _dejaVu.GetGlyphId('W');
        _ttA2 = _dejaVu.GetGlyphId('a');

        _cffA = _sourceSans.GetGlyphId('A');
        _cffV = _sourceSans.GetGlyphId('V');
        _cffT = _sourceSans.GetGlyphId('T');
        _cffO = _sourceSans.GetGlyphId('o');
    }

    [Benchmark(Description = "GetKerning AV (TrueType)")]
    public int KernTT_AV() => _dejaVu.GetKerning(_ttA, _ttV);

    [Benchmark(Description = "GetKerning To (TrueType)")]
    public int KernTT_To() => _dejaVu.GetKerning(_ttT, _ttO);

    [Benchmark(Description = "GetKerning AV (CFF/GPOS)")]
    public int KernCff_AV() => _sourceSans.GetKerning(_cffA, _cffV);

    [Benchmark(Description = "GetKerning To (CFF/GPOS)")]
    public int KernCff_To() => _sourceSans.GetKerning(_cffT, _cffO);

    [Benchmark(Description = "Kern 6 pairs 'AVATO Wa' (TrueType)")]
    public int KernStringTT()
    {
        int total = 0;
        total += _dejaVu.GetKerning(_ttA, _ttV);
        total += _dejaVu.GetKerning(_ttV, _ttA);
        total += _dejaVu.GetKerning(_ttT, _ttO);
        total += _dejaVu.GetKerning(_ttW, _ttA2);
        total += _dejaVu.GetKerning(_ttA, _ttT);
        total += _dejaVu.GetKerning(_ttT, _ttA);
        return total;
    }
}
