using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Shaping;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Script/direction itemization: the F4 Latin fast path (one vectorized <c>ContainsAnyExceptInRange</c>
/// scan → single run) vs the general per-rune <see cref="Script.Get"/> walk, plus the bidi-aware
/// itemizer. The span forms reuse a pooled run list and allocate nothing; the convenience overload
/// that returns a fresh list is included for contrast (MemoryDiagnoser shows the delta).
/// </summary>
[MemoryDiagnoser]
public class ItemizationBenchmarks
{
    private const string LatinSentence = "The quick brown fox jumps over the lazy dog.";
    private const string MixedFilename = "Report_2024_Final(v3).pdf";
    private const string MixedLtrRtl = "Hello مرحبا world";                 // Latin + Arabic
    private const string Cjk = "日本語のテキスト";                     // 日本語のテキスト
    private const string Thai = "สวัสดีชาวโลก"; // สวัสดีชาวโลก

    private readonly List<ScriptRun> _runs = [];

    [Benchmark(Description = "itemize Latin sentence (fast path)")]
    public int Script_Latin() { ScriptItemizer.Itemize(LatinSentence, _runs); return _runs.Count; }

    [Benchmark(Description = "itemize mixed filename (fast path)")]
    public int Script_Filename() { ScriptItemizer.Itemize(MixedFilename, _runs); return _runs.Count; }

    [Benchmark(Description = "itemize Latin+Arabic (general path)")]
    public int Script_MixedLtrRtl() { ScriptItemizer.Itemize(MixedLtrRtl, _runs); return _runs.Count; }

    [Benchmark(Description = "itemize CJK+Kana (general path)")]
    public int Script_Cjk() { ScriptItemizer.Itemize(Cjk, _runs); return _runs.Count; }

    [Benchmark(Description = "itemize Thai (general path)")]
    public int Script_Thai() { ScriptItemizer.Itemize(Thai, _runs); return _runs.Count; }

    [Benchmark(Description = "bidi-itemize Latin (auto level)")]
    public int Bidi_Latin() { BidiScriptItemizer.Itemize(LatinSentence, BidiAlgorithm.AutoLevel, _runs); return _runs.Count; }

    [Benchmark(Description = "bidi-itemize Latin+Arabic (auto level)")]
    public int Bidi_MixedLtrRtl() { BidiScriptItemizer.Itemize(MixedLtrRtl, BidiAlgorithm.AutoLevel, _runs); return _runs.Count; }

    [Benchmark(Description = "itemize Latin — allocating overload (new list)")]
    public int Script_Latin_Allocating() => ScriptItemizer.Itemize(LatinSentence).Count;
}
