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

        // Decode to codepoints and record each one's UTF-16 offset (so runs index the source line).
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        var cps = new uint[count];
        var offsets = new int[count + 1];
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

        var levels = new byte[count];
        var paraLevel = BidiAlgorithm.Resolve(cps, paragraphLevel, levels);

        // Build (level, script) runs in LOGICAL order. A run breaks at a level change or a real
        // script change; Common/Inherited attach to the open run (or fold forward when leading).
        var logical = new List<LogicalRun>();
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
                logical.Add(new LogicalRun(runStart, i, runLevel, haveRealScript ? runScript : DefaultScript));
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
                logical.Add(new LogicalRun(runStart, i, runLevel, runScript));
                runStart = i;
                runScript = s;
                haveRealScript = true;
            }
        }
        logical.Add(new LogicalRun(runStart, count, runLevel, haveRealScript ? runScript : DefaultScript));

        // L2: reorder the runs by embedding level (each run is one unit). Reuse the reordering on a
        // per-run level array so contiguous higher-level run groups reverse together.
        var runLevels = new byte[logical.Count];
        for (var r = 0; r < logical.Count; r++) runLevels[r] = logical[r].Level;
        var visual = new int[logical.Count];
        BidiAlgorithm.Reorder(runLevels, visual);

        foreach (var r in visual)
        {
            var lr = logical[r];
            var start = offsets[lr.StartCp];
            var length = offsets[lr.EndCp] - start;
            var direction = (lr.Level & 1) != 0 ? ShapeDirection.RightToLeft : ShapeDirection.LeftToRight;
            runs.Add(new ScriptRun(start, length, lr.Script, direction));
        }
        return paraLevel;
    }

    private readonly record struct LogicalRun(int StartCp, int EndCp, byte Level, Tag Script);
}
