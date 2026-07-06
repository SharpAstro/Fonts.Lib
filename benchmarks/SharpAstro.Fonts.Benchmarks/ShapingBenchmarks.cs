using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// End-to-end shaping — the number that actually matters. <see cref="Shaper.Shape"/> over a reused
/// <see cref="ShapeBuffer"/>: plain Latin, GSUB ligatures, GPOS kerning, RTL Arabic joining (marks +
/// visual reversal), CJK, and the full bidi-itemize → shape pipeline for mixed text. Steady-state
/// shaping allocates nothing (buffer reused across calls, shape plans cached per font) — the whole
/// point of the F1-F3 work; MemoryDiagnoser verifies the per-op byte count is zero.
/// </summary>
[MemoryDiagnoser]
public class ShapingBenchmarks
{
    private ShapingFont _dejaVu = null!;
    private ShapingFont _notoJp = null!;
    private readonly ShapeBuffer _buf = new();
    private readonly List<ScriptRun> _runs = [];

    private static readonly Tag Latn = new("latn");
    private static readonly Tag Arab = new("arab");
    private static readonly Tag Hani = new("hani");

    private const string Word = "Shaping";
    private const string Sentence = "The quick brown fox jumps over the lazy dog.";
    private const string Ligatures = "office affluent final";   // ffi / ffl / fi ligatures
    private const string Kerning = "AVATAR Wave To Yo";          // several kern pairs
    private const string Arabic = "مرحبا";                       // beh-less greeting, cursive joining
    private const string CjkText = "日本語";                      // 日本語
    private const string Mixed = "File مرحبا v2";                // Latin + Arabic + digits

    [GlobalSetup]
    public void Setup()
    {
        _dejaVu = ShapingFont.Create(OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans)));
        _notoJp = ShapingFont.Create(OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP)));
    }

    private int ShapeLtr(ShapingFont font, string text, Tag script)
    {
        _buf.Clear();
        _buf.Direction = ShapeDirection.LeftToRight;
        _buf.AddText(text);
        Shaper.Shape(font, _buf, script);
        return _buf.Length;
    }

    [Benchmark(Description = "shape Latin word 'Shaping'")]
    public int Latin_Word() => ShapeLtr(_dejaVu, Word, Latn);

    [Benchmark(Description = "shape Latin sentence (44 chars)")]
    public int Latin_Sentence() => ShapeLtr(_dejaVu, Sentence, Latn);

    [Benchmark(Description = "shape ligatures (ffi/ffl/fi)")]
    public int Latin_Ligatures() => ShapeLtr(_dejaVu, Ligatures, Latn);

    [Benchmark(Description = "shape kerning pairs")]
    public int Latin_Kerning() => ShapeLtr(_dejaVu, Kerning, Latn);

    [Benchmark(Description = "shape Arabic (RTL join + marks)")]
    public int Arabic_Rtl()
    {
        _buf.Clear();
        _buf.Direction = ShapeDirection.RightToLeft;
        _buf.AddText(Arabic);
        Shaper.Shape(_dejaVu, _buf, Arab);
        return _buf.Length;
    }

    [Benchmark(Description = "shape CJK (NotoSansJP)")]
    public int Cjk() => ShapeLtr(_notoJp, CjkText, Hani);

    [Benchmark(Description = "full pipeline: bidi-itemize + shape (mixed LTR/RTL)")]
    public int Pipeline_Mixed()
    {
        BidiScriptItemizer.Itemize(Mixed, BidiAlgorithm.AutoLevel, _runs);
        var total = 0;
        foreach (var run in _runs)
        {
            _buf.Clear();
            _buf.Direction = run.Direction;
            _buf.AddText(Mixed.AsSpan(run.Start, run.Length), run.Start);
            Shaper.Shape(_dejaVu, _buf, run.Script);
            total += _buf.Length;
        }
        return total;
    }
}
