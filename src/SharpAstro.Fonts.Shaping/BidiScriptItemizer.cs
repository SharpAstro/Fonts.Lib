using System.Collections.Generic;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// Bidi-aware itemization (UAX #9 level A): resolves a paragraph's embedding levels with
/// <see cref="BidiAlgorithm"/>, segments it into maximal single-level, single-script runs, and
/// returns them in <b>visual</b> (left-to-right) order — each run tagged with its script and
/// direction, ready to hand to <see cref="Shaper.Shape"/> one after another. The shaper reverses an
/// RTL run's glyphs internally, so appending the shaped runs in the returned order yields correct
/// mixed-direction placement.
///
/// <para>This is the full-bidi counterpart to <see cref="ScriptItemizer"/> (which does only
/// script-implied direction, no reordering). Common/Inherited characters attach to the surrounding
/// run exactly as there; the added ingredient is that a run also breaks at an embedding-level change
/// and the runs are reordered by rule L2.</para>
/// </summary>
public static class BidiScriptItemizer
{
    private static readonly Tag DefaultScript = new("latn");

    /// <summary>Itemize <paramref name="text"/> (one paragraph) into visual-order runs in
    /// <paramref name="runs"/> (cleared first). <paramref name="paragraphLevel"/> is 0 (LTR),
    /// 1 (RTL), or <see cref="BidiAlgorithm.AutoLevel"/> to derive it (P2/P3). Returns the resolved
    /// paragraph level.</summary>
    public static byte Itemize(ReadOnlySpan<char> text, int paragraphLevel, List<ScriptRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        runs.Clear();
        if (text.IsEmpty)
            return (byte)(paragraphLevel == 1 ? 1 : 0);

        // Latin fast path (mirrors ScriptItemizer): an LTR/auto paragraph whose text stays within
        // Basic Latin .. Latin Extended-B (<= U+024F) has no RTL/AL/explicit characters, so it is a
        // single Latin/LTR run at paragraph level 0 — skip the whole bidi resolution and its
        // allocations. This keeps plain UI text as cheap through the bidi adapter as it was before.
        if (paragraphLevel is 0 or BidiAlgorithm.AutoLevel && !text.ContainsAnyExceptInRange('\u0000', '\u024F'))
        {
            runs.Add(new ScriptRun(0, text.Length, DefaultScript, ShapeDirection.LeftToRight));
            return 0;
        }

        // Decode to codepoints and record each one's UTF-16 offset (so runs index the source line),
        // into per-thread scratch — the whole non-fast path is now zero-allocation.
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;

        var sc = _scratch ??= new Scratch();
        sc.EnsureCapacity(count);
        var cps = sc.Cps;
        var offsets = sc.Offsets;
        var ci = 0;
        var off = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            cps[ci] = (uint)rune.Value;
            offsets[ci] = off;
            off += rune.Utf16SequenceLength;
            ci++;
        }
        offsets[count] = text.Length;

        var levels = sc.Levels;
        var paraLevel = BidiAlgorithm.Resolve(cps.AsSpan(0, count), paragraphLevel, levels.AsSpan(0, count));

        // Build (level, script) runs in LOGICAL order into parallel scratch arrays. A run breaks at a
        // level change or a real script change; Common/Inherited attach to the open run (or fold
        // forward when leading).
        var rStart = sc.RunStartCp;
        var rEnd = sc.RunEndCp;
        var rLevel = sc.RunLevel;
        var rScript = sc.RunScript;
        var logicalCount = 0;
        var runStart = 0;
        var runLevel = levels[0];
        var runScript = Script.Get(cps[0]);
        var haveRealScript = runScript != Script.Common && runScript != Script.Inherited;
        for (var i = 1; i < count; i++)
        {
            var lvl = levels[i];
            var s = Script.Get(cps[i]);
            var neutral = s == Script.Common || s == Script.Inherited;

            if (lvl != runLevel)
            {
                rStart[logicalCount] = runStart; rEnd[logicalCount] = i; rLevel[logicalCount] = runLevel;
                rScript[logicalCount++] = haveRealScript ? runScript : DefaultScript;
                runStart = i;
                runLevel = lvl;
                runScript = s;
                haveRealScript = !neutral;
            }
            else if (!neutral && !haveRealScript)
            {
                runScript = s; // leading neutrals fold into the first real script
                haveRealScript = true;
            }
            else if (!neutral && s != runScript)
            {
                rStart[logicalCount] = runStart; rEnd[logicalCount] = i; rLevel[logicalCount] = runLevel;
                rScript[logicalCount++] = runScript;
                runStart = i;
                runScript = s;
                haveRealScript = true;
            }
        }
        rStart[logicalCount] = runStart; rEnd[logicalCount] = count; rLevel[logicalCount] = runLevel;
        rScript[logicalCount++] = haveRealScript ? runScript : DefaultScript;

        // L2: reorder the runs by embedding level (each run is one unit); the per-run level array is
        // rLevel itself, so contiguous higher-level run groups reverse together.
        var visual = sc.Visual;
        BidiAlgorithm.Reorder(rLevel.AsSpan(0, logicalCount), visual.AsSpan(0, logicalCount));

        for (var v = 0; v < logicalCount; v++)
        {
            var r = visual[v];
            var start = offsets[rStart[r]];
            var length = offsets[rEnd[r]] - start;
            var direction = (rLevel[r] & 1) != 0 ? ShapeDirection.RightToLeft : ShapeDirection.LeftToRight;
            runs.Add(new ScriptRun(start, length, rScript[r], direction));
        }
        return paraLevel;
    }

    // Per-thread reusable buffers for the non-fast path (mirrors BidiAlgorithm's scratch): codepoints,
    // UTF-16 offsets, levels, the logical (level, script) runs as parallel arrays, and the L2 visual
    // order. Grown to the largest paragraph seen; there are at most `count` logical runs.
    private sealed class Scratch
    {
        public uint[] Cps = [];
        public int[] Offsets = [];
        public byte[] Levels = [];
        public int[] RunStartCp = [];
        public int[] RunEndCp = [];
        public byte[] RunLevel = [];
        public Tag[] RunScript = [];
        public int[] Visual = [];

        public void EnsureCapacity(int count)
        {
            if (Cps.Length >= count) return;
            var cap = Math.Max(count, 64);
            Cps = new uint[cap];
            Offsets = new int[cap + 1];
            Levels = new byte[cap];
            RunStartCp = new int[cap];
            RunEndCp = new int[cap];
            RunLevel = new byte[cap];
            RunScript = new Tag[cap];
            Visual = new int[cap];
        }
    }

    [ThreadStatic] private static Scratch? _scratch;
}
