using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// The F4 page-index (two-stage "trie") vs the plain binary search it replaced, over the same
/// Script range table (984 ranges). The paged path dispatches on <c>cp &gt;&gt; 8</c> and scans
/// only the ranges overlapping that 256-codepoint page; the binary path searches all ~1000.
/// <see cref="Script.Get"/> is the real shipping call (paged + <c>Tag</c> wrap). Every variant is
/// zero-alloc — MemoryDiagnoser confirms it. Uses InternalsVisibleTo to reach the internal tables.
/// </summary>
[MemoryDiagnoser]
public class ScriptLookupBenchmarks
{
    private static readonly uint Common = Script.Common.Value;

    // A realistic multi-script spread: Latin, Greek, Cyrillic, Hebrew, Arabic, Devanagari, Thai,
    // Hiragana/Katakana, CJK, Hangul, Mongolian, punctuation, emoji, and a couple of SMP codepoints.
    private static readonly uint[] Mixed =
    [
        0x0041, 0x0061, 0x0031, 0x00E9, 0x0141, 0x0391, 0x03B1, 0x0410, 0x05D0, 0x05EA,
        0x0628, 0x0645, 0x0905, 0x0E01, 0x0E43, 0x3042, 0x30AB, 0x4E00, 0x4FAE, 0x9FA5,
        0xAC00, 0xD7A3, 0x1820, 0x1846, 0x0020, 0x002E, 0x2013, 0x1F600, 0x1F914, 0x20000,
    ];

    // The pathological page: Arabic Mathematical Alphabetic Symbols (U+1EE00..) alternates assigned
    // letters with unassigned holes — ~35 ranges land in this one 256-cp page (paged's worst case).
    private static readonly uint[] WorstPage = BuildRange(0x1EE00, 0x1EE40);

    private static uint[] BuildRange(uint start, uint endExclusive)
    {
        var a = new uint[endExclusive - start];
        for (var i = 0u; i < a.Length; i++) a[i] = start + i;
        return a;
    }

    [Benchmark(Description = "paged lookup — mixed scripts (30 cps)")]
    public uint Paged_Mixed()
    {
        ReadOnlySpan<byte> ranges = Script.Ranges, pages = Script.PageIndex;
        uint acc = 0;
        foreach (var cp in Mixed) acc ^= UcdTables.RangeU32Paged(ranges, pages, cp, Common);
        return acc;
    }

    [Benchmark(Description = "binary search — mixed scripts (30 cps)")]
    public uint Binary_Mixed()
    {
        ReadOnlySpan<byte> ranges = Script.Ranges;
        uint acc = 0;
        foreach (var cp in Mixed) acc ^= UcdTables.RangeU32(ranges, cp, Common);
        return acc;
    }

    [Benchmark(Description = "paged — worst-case fragmented page (64 cps)")]
    public uint Paged_WorstPage()
    {
        ReadOnlySpan<byte> ranges = Script.Ranges, pages = Script.PageIndex;
        uint acc = 0;
        foreach (var cp in WorstPage) acc ^= UcdTables.RangeU32Paged(ranges, pages, cp, Common);
        return acc;
    }

    [Benchmark(Description = "binary — worst-case fragmented page (64 cps)")]
    public uint Binary_WorstPage()
    {
        ReadOnlySpan<byte> ranges = Script.Ranges;
        uint acc = 0;
        foreach (var cp in WorstPage) acc ^= UcdTables.RangeU32(ranges, cp, Common);
        return acc;
    }

    [Benchmark(Description = "Script.Get real API (paged + Tag) — mixed")]
    public uint Get_Mixed()
    {
        uint acc = 0;
        foreach (var cp in Mixed) acc ^= Script.Get(cp).Value;
        return acc;
    }
}
