using BenchmarkDotNet.Attributes;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Measures the full parse cost of <see cref="OpenTypeFont.Load(byte[], int)"/>
/// across font formats — TrueType, CFF/OTF, variable, and large CJK.
/// </summary>
[MemoryDiagnoser]
public class FontLoadingBenchmarks
{
    private byte[] _dejaVuBytes = null!;
    private byte[] _sourceSansBytes = null!;
    private byte[] _robotoFlexBytes = null!;
    private byte[] _notoSansJPBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dejaVuBytes    = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        _sourceSansBytes = File.ReadAllBytes(Fixtures.Path(Fixtures.SourceSans3));
        _robotoFlexBytes = File.ReadAllBytes(Fixtures.Path(Fixtures.RobotoFlex));
        _notoSansJPBytes = File.ReadAllBytes(Fixtures.Path(Fixtures.NotoSansJP));
    }

    [Benchmark(Description = "Load TrueType (DejaVu Sans)")]
    public OpenTypeFont LoadTrueType() => OpenTypeFont.Load(_dejaVuBytes);

    [Benchmark(Description = "Load CFF/OTF (Source Sans 3)")]
    public OpenTypeFont LoadCff() => OpenTypeFont.Load(_sourceSansBytes);

    [Benchmark(Description = "Load Variable (Roboto Flex)")]
    public OpenTypeFont LoadVariable() => OpenTypeFont.Load(_robotoFlexBytes);

    [Benchmark(Description = "Load CJK CFF (Noto Sans JP, ~16k glyphs)")]
    public OpenTypeFont LoadCjk() => OpenTypeFont.Load(_notoSansJPBytes);
}
