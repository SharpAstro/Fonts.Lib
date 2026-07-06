using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// A maximal single-script, single-direction slice of a line — the unit <see cref="Shaper.Shape"/>
/// consumes. <see cref="Start"/>/<see cref="Length"/> are UTF-16 code-unit offsets into the
/// itemized text, so a run maps straight to <c>text.Slice(Start, Length)</c> and its clusters
/// index the line.
/// </summary>
public readonly record struct ScriptRun(int Start, int Length, Tag Script, ShapeDirection Direction);

/// <summary>
/// Splits a line into script/direction runs — level B: script-implied direction, logical order.
/// The per-codepoint Script property groups the run; Common (punctuation, digits, spaces) and
/// Inherited (combining marks) attach to the preceding run, or forward to the first real script
/// when they lead. Direction comes from a fixed RTL-script set; runs are emitted in logical order
/// and each RTL run is later reversed to visual order by the shaper.
///
/// <para>This is the font-free, pure-Unicode front half of the pipeline: itemize a line into runs,
/// then shape each run with <see cref="Shaper.Shape"/> under its <see cref="ScriptRun.Script"/> and
/// <see cref="ScriptRun.Direction"/>. Bidi edge cases — digits adjacent to RTL, neutral runs between
/// opposite-direction scripts, RTL paragraph context — are out of scope here and handled by the
/// UAX #9 algorithm (H6). This covers pure runs and the common mixed filename in an LTR UI.</para>
/// </summary>
public static class ScriptItemizer
{
    private static readonly Tag DefaultScript = new("latn");

    /// <summary>Itemize <paramref name="text"/> into a fresh list of runs (logical order).</summary>
    public static List<ScriptRun> Itemize(ReadOnlySpan<char> text)
    {
        var runs = new List<ScriptRun>();
        Itemize(text, runs);
        return runs;
    }

    /// <summary>Itemize <paramref name="text"/> into <paramref name="runs"/> (cleared first) — the
    /// allocation-free form for a pooled list.</summary>
    public static void Itemize(ReadOnlySpan<char> text, List<ScriptRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        runs.Clear();
        if (text.IsEmpty) return;

        // Fast path: a line entirely within Basic Latin .. Latin Extended-B (<= U+024F) is a single
        // Latin/LTR run — every codepoint in that range is Latin or Common, all left-to-right, and
        // there are no combining marks (which start at U+0300). One vectorized scan replaces the
        // per-rune Script.Get walk for the overwhelmingly common plain-UI-text case. (Matches the
        // general path below, whose first real script for such text is always latn == DefaultScript.)
        if (!text.ContainsAnyExceptInRange('\u0000', '\u024F'))
        {
            runs.Add(new ScriptRun(0, text.Length, DefaultScript, ShapeDirection.LeftToRight));
            return;
        }

        var runStart = 0;
        var haveScript = false;
        var current = Script.Common; // the open run's resolved script, once haveScript is set
        var pos = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var script = Script.Get((uint)rune.Value);

            // Common/Inherited never open or close a run: they attach to the current one (or, when
            // leading, fold forward into the first real script's run since runStart stays put).
            if (script != Script.Common && script != Script.Inherited)
            {
                if (!haveScript)
                {
                    current = script;
                    haveScript = true;
                }
                else if (script != current)
                {
                    runs.Add(new ScriptRun(runStart, pos - runStart, current, DirectionOf(current)));
                    runStart = pos;
                    current = script;
                }
            }

            pos += rune.Utf16SequenceLength;
        }

        // The final run covers the tail (including trailing Common/Inherited). A line with no real
        // script at all (pure digits/punctuation) resolves to the default so it still shapes LTR.
        var runScript = haveScript ? current : DefaultScript;
        runs.Add(new ScriptRun(runStart, pos - runStart, runScript, DirectionOf(runScript)));
    }

    private static ShapeDirection DirectionOf(Tag script)
        => Script.IsRightToLeft(script) ? ShapeDirection.RightToLeft : ShapeDirection.LeftToRight;
}
