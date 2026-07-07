using System.Text;
using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Shaping;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// The UAX #9 core: <see cref="BidiAlgorithm.Resolve"/> on pure-LTR text (should be near-free — the
/// all-strong-L early exit), RTL mixed with Latin and European numbers (weak-type resolution), and
/// bracket/isolate-heavy input (N0 paired brackets + BD16); plus L2 <see cref="BidiAlgorithm.Reorder"/>.
/// Level and visual-order buffers are reused, so steady-state allocation is zero.
/// </summary>
[MemoryDiagnoser]
public class BidiBenchmarks
{
    private uint[] _latin = null!, _rtl = null!, _brackets = null!;
    private byte[] _levels = null!;
    private int[] _visual = null!;

    [GlobalSetup]
    public void Setup()
    {
        _latin = ToCodepoints("The quick brown fox jumps over the lazy dog.");
        // Hebrew + Arabic with Latin and European numbers interspersed (W1-W7 + I1/I2 work).
        _rtl = ToCodepoints("אבג abc 123 مرحبا 456");
        // Nested brackets around RTL runs, plus an LRI/PDI isolate (N0 + BD16 + X-rules).
        _brackets = ToCodepoints("a (א [ב] ג) ⁦b⁩ c");
        var max = Math.Max(_latin.Length, Math.Max(_rtl.Length, _brackets.Length));
        _levels = new byte[max];
        _visual = new int[max];
    }

    private static uint[] ToCodepoints(string s)
    {
        var list = new List<uint>(s.Length);
        foreach (var r in s.EnumerateRunes()) list.Add((uint)r.Value);
        return [.. list];
    }

    [Benchmark(Description = "Resolve pure Latin (all-L fast exit)")]
    public byte Resolve_Latin() => BidiAlgorithm.Resolve(_latin, BidiAlgorithm.AutoLevel, _levels.AsSpan(0, _latin.Length));

    [Benchmark(Description = "Resolve RTL + Latin + numbers")]
    public byte Resolve_Rtl() => BidiAlgorithm.Resolve(_rtl, BidiAlgorithm.AutoLevel, _levels.AsSpan(0, _rtl.Length));

    [Benchmark(Description = "Resolve brackets + isolates")]
    public byte Resolve_Brackets() => BidiAlgorithm.Resolve(_brackets, BidiAlgorithm.AutoLevel, _levels.AsSpan(0, _brackets.Length));

    [Benchmark(Description = "Resolve + Reorder (L2) an RTL line")]
    public int ResolveAndReorder_Rtl()
    {
        var levels = _levels.AsSpan(0, _rtl.Length);
        BidiAlgorithm.Resolve(_rtl, BidiAlgorithm.AutoLevel, levels);
        BidiAlgorithm.Reorder(levels, _visual.AsSpan(0, _rtl.Length));
        return _visual[0];
    }
}
