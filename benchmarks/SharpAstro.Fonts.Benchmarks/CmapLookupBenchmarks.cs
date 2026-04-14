using BenchmarkDotNet.Attributes;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Measures cmap glyph-id lookup speed across subtable formats:
/// Format 4 (BMP binary search), Format 12 (full-range), Format 14 (IVS).
/// </summary>
[MemoryDiagnoser]
public class CmapLookupBenchmarks
{
    private OpenTypeFont _dejaVu = null!;
    private OpenTypeFont _notoJP = null!;

    // Representative codepoints.
    private const uint LatinA       = 'A';          // U+0041 — BMP, format 4
    private const uint CyrillicZhe  = 0x0416;       // U+0416 Ж — BMP
    private const uint CjkUnified   = 0x4FAE;       // U+4FAE 侮 — BMP CJK
    private const uint Emoji        = 0x1F600;       // U+1F600 😀 — SMP, format 12
    private const uint VarSelector  = 0xFE00;       // VS1 — for format 14

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        _notoJP = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
    }

    [Benchmark(Description = "cmap Latin 'A' (format 4)")]
    public uint LookupLatinA() => _dejaVu.GetGlyphId(LatinA);

    [Benchmark(Description = "cmap Cyrillic Ж (format 4)")]
    public uint LookupCyrillic() => _dejaVu.GetGlyphId(CyrillicZhe);

    [Benchmark(Description = "cmap CJK U+4FAE (CJK font)")]
    public uint LookupCjk() => _notoJP.GetGlyphId(CjkUnified);

    [Benchmark(Description = "cmap Emoji U+1F600 (format 12)")]
    public uint LookupEmoji() => _dejaVu.GetGlyphId(Emoji);

    [Benchmark(Description = "cmap IVS U+4FAE+VS1 (format 14)")]
    public uint LookupIvs() => _notoJP.GetGlyphId(CjkUnified, VarSelector);

    [Benchmark(Description = "cmap 100 sequential Latin codepoints")]
    public uint LookupLatinBatch()
    {
        uint last = 0;
        for (uint cp = 0x20; cp < 0x84; cp++)
            last = _dejaVu.GetGlyphId(cp);
        return last;
    }

    [Benchmark(Description = "cmap 100 sequential CJK codepoints")]
    public uint LookupCjkBatch()
    {
        uint last = 0;
        for (uint cp = 0x4E00; cp < 0x4E64; cp++)
            last = _notoJP.GetGlyphId(cp);
        return last;
    }
}
