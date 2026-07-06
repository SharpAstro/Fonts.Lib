using System.Collections.Generic;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) — resolves an embedding level for every character
/// of a paragraph and reorders a line from logical to visual order. Implements the modern
/// (isolate-aware) rules: P2–P3 (paragraph level), X1–X10 (explicit levels, overrides, and
/// isolates), W1–W7 (weak types), N0 (paired brackets), N1–N2 (neutrals), I1–I2 (implicit levels),
/// L1 (level resets) and L2 (reordering).
///
/// <para>This is the front half the engine lacked: <see cref="ScriptItemizer"/> did only
/// "level B" script-implied direction. With real levels, a mixed LTR/RTL paragraph orders
/// correctly. The shaper still consumes single-direction runs — the adapter splits a resolved
/// paragraph into level runs and hands each to <see cref="Shaper.Shape"/>.</para>
///
/// <para>Reference: https://www.unicode.org/reports/tr9/ and the Unicode BidiReference.</para>
/// </summary>
public static class BidiAlgorithm
{
    /// <summary>Max explicit embedding depth (UAX #9 BD2).</summary>
    private const int MaxDepth = 125;

    /// <summary>Auto paragraph level (resolve via P2/P3 — first strong character).</summary>
    public const int AutoLevel = -1;

    /// <summary>
    /// Resolve the embedding level of every codepoint in <paramref name="codepoints"/> (one
    /// paragraph). <paramref name="paragraphLevel"/> is 0 (LTR), 1 (RTL), or <see cref="AutoLevel"/>
    /// to derive it from the first strong character (P2/P3). Fills <paramref name="levels"/> (same
    /// length as <paramref name="codepoints"/>) with post-resolution levels and returns the resolved
    /// paragraph level. Characters removed by rule X9 (explicit formatting) keep the level of their
    /// surroundings; use <see cref="Reorder"/> for the L2 visual order.
    /// </summary>
    public static byte Resolve(ReadOnlySpan<uint> codepoints, int paragraphLevel, Span<byte> levels)
    {
        var n = codepoints.Length;
        if (levels.Length < n)
            throw new ArgumentException("levels span is shorter than codepoints.", nameof(levels));
        if (n == 0)
            return (byte)(paragraphLevel == 1 ? 1 : 0);

        var types = new BidiClass[n];
        var original = new BidiClass[n];
        for (var i = 0; i < n; i++)
        {
            var t = Bidi.Get(codepoints[i]);
            types[i] = t;
            original[i] = t;
        }

        // BD9: for each isolate initiator, the index of its matching PDI (or n if none).
        var matchingPDI = ComputeMatchingPDI(types);

        var paraLevel = paragraphLevel is 0 or 1
            ? (byte)paragraphLevel
            : ComputeParagraphLevel(types, matchingPDI, 0, n);

        // X1–X8: explicit levels + override resolution, using the directional status stack.
        var levelArr = new byte[n];
        ResolveExplicit(types, matchingPDI, paraLevel, levelArr);

        // X9: remove explicit formatting characters (treat as BN for the rest of the algorithm).
        for (var i = 0; i < n; i++)
            if (IsRemovedByX9(original[i]))
                types[i] = BidiClass.BN;

        // X10: process each isolating run sequence (W, N, I rules run per sequence).
        foreach (var seq in BuildIsolatingRunSequences(types, original, levelArr, paraLevel, matchingPDI))
        {
            ResolveWeakTypes(seq, types);
            ResolveNeutralTypes(seq, types, codepoints, levelArr);
            ResolveImplicitLevels(seq, types, levelArr);
        }

        // L1: reset separators and trailing whitespace/isolates to the paragraph level.
        ApplyL1(original, levelArr, paraLevel);

        // UAX #9 §5.2 (retaining format characters): give each X9-removed character the level of the
        // preceding character, so L2 treats it as part of the adjacent run (it is omitted from
        // display). Without this its stale level fractures the surrounding run and misorders it.
        for (var i = 0; i < n; i++)
            if (IsRemovedByX9(original[i]))
                levelArr[i] = i > 0 ? levelArr[i - 1] : paraLevel;

        for (var i = 0; i < n; i++)
            levels[i] = levelArr[i];
        return paraLevel;
    }

    /// <summary>
    /// L2 reordering: given per-character <paramref name="levels"/> for one line, fill
    /// <paramref name="visualToLogical"/> so that <c>visualToLogical[v]</c> is the logical index
    /// drawn at visual position <c>v</c> (left to right). Reverses each contiguous run from the
    /// highest level down to the lowest odd level.
    /// </summary>
    public static void Reorder(ReadOnlySpan<byte> levels, Span<int> visualToLogical)
    {
        var n = levels.Length;
        for (var i = 0; i < n; i++)
            visualToLogical[i] = i;
        if (n == 0)
            return;

        byte highest = 0;
        var lowestOdd = (byte)MaxDepth + 1;
        for (var i = 0; i < n; i++)
        {
            var l = levels[i];
            if (l > highest) highest = l;
            if ((l & 1) != 0 && l < lowestOdd) lowestOdd = l;
        }

        for (var level = highest; level >= lowestOdd; level--)
        {
            var i = 0;
            while (i < n)
            {
                if (levels[i] < level) { i++; continue; }
                var start = i;
                while (i < n && levels[i] >= level) i++;
                // reverse visualToLogical[start..i)
                for (int a = start, b = i - 1; a < b; a++, b--)
                    (visualToLogical[a], visualToLogical[b]) = (visualToLogical[b], visualToLogical[a]);
            }
        }
    }

    // ---- P2/P3 -------------------------------------------------------------------------

    // The paragraph (or FSI) embedding level: first strong type (L→0, AL/R→1) scanning [start,end),
    // skipping any characters between an isolate initiator and its matching PDI. Default 0.
    private static byte ComputeParagraphLevel(BidiClass[] types, int[] matchingPDI, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var t = types[i];
            if (t is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
            {
                i = matchingPDI[i]; // skip the isolated sequence (i advances to the PDI, loop ++ moves past)
                continue;
            }
            if (t == BidiClass.L) return 0;
            if (t is BidiClass.R or BidiClass.AL) return 1;
        }
        return 0;
    }

    // ---- BD9: match isolate initiators to PDIs -----------------------------------------

    private static int[] ComputeMatchingPDI(BidiClass[] types)
    {
        var n = types.Length;
        var matching = new int[n];
        var stack = new Stack<int>();
        for (var i = 0; i < n; i++)
        {
            switch (types[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    matching[i] = n; // default: no matching PDI
                    stack.Push(i);
                    break;
                case BidiClass.PDI when stack.Count > 0:
                    matching[stack.Pop()] = i;
                    break;
            }
        }
        return matching;
    }

    // ---- X1–X8: explicit levels & directions -------------------------------------------

    private readonly record struct StatusEntry(byte Level, BidiClass Override, bool Isolate);

    private static void ResolveExplicit(BidiClass[] types, int[] matchingPDI, byte paraLevel, byte[] levels)
    {
        var n = types.Length;
        var stack = new Stack<StatusEntry>();
        stack.Push(new StatusEntry(paraLevel, BidiClass.ON, false)); // ON = neutral override status

        var overflowIsolate = 0;
        var overflowEmbedding = 0;
        var validIsolate = 0;

        for (var i = 0; i < n; i++)
        {
            var t = types[i];
            switch (t)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    levels[i] = stack.Peek().Level; // X9 char; keep current level for now
                    var isRtl = t is BidiClass.RLE or BidiClass.RLO;
                    var newLevel = NextLevel(stack.Peek().Level, isRtl);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        var ov = t == BidiClass.LRO ? BidiClass.L : t == BidiClass.RLO ? BidiClass.R : BidiClass.ON;
                        stack.Push(new StatusEntry((byte)newLevel, ov, false));
                    }
                    else if (overflowIsolate == 0)
                    {
                        overflowEmbedding++;
                    }
                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    var cur = stack.Peek();
                    levels[i] = cur.Level;
                    if (cur.Override != BidiClass.ON) types[i] = cur.Override;

                    var isRtl = t == BidiClass.RLI
                        || (t == BidiClass.FSI && ComputeParagraphLevel(types, matchingPDI, i + 1, matchingPDI[i]) == 1);
                    var newLevel = NextLevel(cur.Level, isRtl);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        validIsolate++;
                        stack.Push(new StatusEntry((byte)newLevel, BidiClass.ON, true));
                    }
                    else
                    {
                        overflowIsolate++;
                    }
                    break;
                }
                case BidiClass.PDI:
                {
                    if (overflowIsolate > 0)
                    {
                        overflowIsolate--;
                    }
                    else if (validIsolate > 0)
                    {
                        overflowEmbedding = 0;
                        while (!stack.Peek().Isolate) stack.Pop();
                        stack.Pop();
                        validIsolate--;
                    }
                    var cur = stack.Peek();
                    levels[i] = cur.Level;
                    if (cur.Override != BidiClass.ON) types[i] = cur.Override;
                    break;
                }
                case BidiClass.PDF:
                {
                    levels[i] = stack.Peek().Level;
                    if (overflowIsolate > 0) { /* nothing */ }
                    else if (overflowEmbedding > 0) overflowEmbedding--;
                    else if (!stack.Peek().Isolate && stack.Count >= 2) stack.Pop();
                    break;
                }
                case BidiClass.B:
                {
                    // X8: paragraph separator terminates all embeddings; reset to paragraph level.
                    levels[i] = paraLevel;
                    break;
                }
                case BidiClass.BN:
                {
                    levels[i] = stack.Peek().Level;
                    break;
                }
                default:
                {
                    var cur = stack.Peek();
                    levels[i] = cur.Level;
                    if (cur.Override != BidiClass.ON) types[i] = cur.Override;
                    break;
                }
            }
        }
    }

    // Least level greater than `level` with the requested parity (odd for RTL, even for LTR).
    private static int NextLevel(byte level, bool rtl)
        => rtl ? (level + 1) | 1 : (level + 2) & ~1;

    private static bool IsRemovedByX9(BidiClass t)
        => t is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;

    // ---- X10: isolating run sequences --------------------------------------------------

    /// <summary>An isolating run sequence: the ordered character indices plus the start-of-sequence
    /// and end-of-sequence directional types (sos/eos) that bound its weak/neutral resolution.</summary>
    private sealed class RunSequence
    {
        public required int[] Indices;
        public BidiClass Sos;
        public BidiClass Eos;
    }

    private static List<RunSequence> BuildIsolatingRunSequences(
        BidiClass[] types, BidiClass[] original, byte[] levels, byte paraLevel, int[] matchingPDI)
    {
        var n = types.Length;

        // Level runs: maximal runs of equal level, skipping X9-removed characters entirely.
        var runs = new List<List<int>>();
        List<int>? current = null;
        var currentLevel = -1;
        for (var i = 0; i < n; i++)
        {
            if (IsRemovedByX9(original[i])) continue;
            if (current is null || levels[i] != currentLevel)
            {
                current = [];
                runs.Add(current);
                currentLevel = levels[i];
            }
            current.Add(i);
        }

        // Chain level runs into isolating run sequences: a run ending in an isolate initiator whose
        // matching PDI opens another run continues into that run (BD13).
        var sequences = new List<RunSequence>();
        var used = new bool[runs.Count];

        // Map: first index of a run -> run number, to find the run a PDI starts.
        var runStartingAt = new Dictionary<int, int>();
        for (var r = 0; r < runs.Count; r++)
            runStartingAt[runs[r][0]] = r;

        for (var r = 0; r < runs.Count; r++)
        {
            if (used[r]) continue;
            // A sequence starts at a run whose first char is not a PDI matching an isolate initiator.
            var firstIdx = runs[r][0];
            if (original[firstIdx] == BidiClass.PDI && HasMatchingInitiator(matchingPDI, firstIdx))
                continue;

            var seqIndices = new List<int>();
            var cur = r;
            while (true)
            {
                used[cur] = true;
                seqIndices.AddRange(runs[cur]);
                var last = runs[cur][^1];
                if (original[last] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI
                    && matchingPDI[last] < n
                    && runStartingAt.TryGetValue(matchingPDI[last], out var nextRun))
                {
                    cur = nextRun;
                    continue;
                }
                break;
            }

            var seq = new RunSequence { Indices = [.. seqIndices] };
            ComputeSosEos(seq, types, levels, paraLevel, n);
            sequences.Add(seq);
        }
        return sequences;
    }

    private static bool HasMatchingInitiator(int[] matchingPDI, int pdiIndex)
    {
        for (var i = 0; i < matchingPDI.Length; i++)
            if (matchingPDI[i] == pdiIndex)
                return true;
        return false;
    }

    // sos/eos (X10): compare the sequence's boundary level with the adjacent character's level
    // (or the paragraph level at the text edges); the higher level's parity gives L or R.
    private static void ComputeSosEos(RunSequence seq, BidiClass[] types, byte[] levels, byte paraLevel, int n)
    {
        var first = seq.Indices[0];
        var last = seq.Indices[^1];
        var seqLevel = levels[first];

        var prevLevel = paraLevel;
        for (var i = first - 1; i >= 0; i--)
        {
            if (IsRemovedByX9Level(types, i)) continue;
            prevLevel = levels[i];
            break;
        }
        seq.Sos = ((Math.Max(seqLevel, prevLevel) & 1) != 0) ? BidiClass.R : BidiClass.L;

        // eos: if the sequence ends with an isolate initiator that has no matching PDI, use the
        // paragraph level; otherwise the following character's level.
        var endLevel = levels[last];
        var nextLevel = paraLevel;
        if (types[last] is not (BidiClass.LRI or BidiClass.RLI or BidiClass.FSI))
        {
            for (var i = last + 1; i < n; i++)
            {
                if (IsRemovedByX9Level(types, i)) continue;
                nextLevel = levels[i];
                break;
            }
        }
        seq.Eos = ((Math.Max(endLevel, nextLevel) & 1) != 0) ? BidiClass.R : BidiClass.L;
    }

    private static bool IsRemovedByX9Level(BidiClass[] types, int i) => types[i] == BidiClass.BN;

    // ---- W1–W7 -------------------------------------------------------------------------

    private static void ResolveWeakTypes(RunSequence seq, BidiClass[] types)
    {
        var idx = seq.Indices;
        var count = idx.Length;

        // W1: NSM → type of previous character in the sequence (sos at the start); isolate
        // initiators and PDI count as ON for this purpose.
        var prev = seq.Sos;
        for (var k = 0; k < count; k++)
        {
            var t = types[idx[k]];
            if (t == BidiClass.NSM)
            {
                types[idx[k]] = prev is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                    ? BidiClass.ON : prev;
            }
            prev = types[idx[k]];
        }

        // W2: EN → AN if the last strong type seen is AL.
        var lastStrong = seq.Sos;
        for (var k = 0; k < count; k++)
        {
            var t = types[idx[k]];
            if (t == BidiClass.EN && lastStrong == BidiClass.AL) types[idx[k]] = BidiClass.AN;
            if (t is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = t;
        }

        // W3: AL → R.
        for (var k = 0; k < count; k++)
            if (types[idx[k]] == BidiClass.AL) types[idx[k]] = BidiClass.R;

        // W4: a single ES between two EN → EN; a single CS between two numbers of the same type → that type.
        for (var k = 1; k < count - 1; k++)
        {
            var t = types[idx[k]];
            var before = types[idx[k - 1]];
            var after = types[idx[k + 1]];
            if (t == BidiClass.ES && before == BidiClass.EN && after == BidiClass.EN)
                types[idx[k]] = BidiClass.EN;
            else if (t == BidiClass.CS && before == after && before is BidiClass.EN or BidiClass.AN)
                types[idx[k]] = before;
        }

        // W5: a sequence of ET adjacent to EN → EN.
        for (var k = 0; k < count; k++)
        {
            if (types[idx[k]] != BidiClass.ET) continue;
            var runStart = k;
            while (k < count && types[idx[k]] == BidiClass.ET) k++;
            var runEnd = k; // exclusive
            var adjacentEN = (runStart > 0 && types[idx[runStart - 1]] == BidiClass.EN)
                || (runEnd < count && types[idx[runEnd]] == BidiClass.EN);
            if (adjacentEN)
                for (var j = runStart; j < runEnd; j++) types[idx[j]] = BidiClass.EN;
            k = runEnd - 1;
        }

        // W6: remaining ES/ET/CS → ON.
        for (var k = 0; k < count; k++)
            if (types[idx[k]] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
                types[idx[k]] = BidiClass.ON;

        // W7: EN → L if the last strong type is L.
        lastStrong = seq.Sos;
        for (var k = 0; k < count; k++)
        {
            var t = types[idx[k]];
            if (t == BidiClass.EN && lastStrong == BidiClass.L) types[idx[k]] = BidiClass.L;
            if (t is BidiClass.L or BidiClass.R) lastStrong = t;
        }
    }

    // ---- N0: paired brackets -----------------------------------------------------------

    private static void ResolveNeutralTypes(RunSequence seq, BidiClass[] types, ReadOnlySpan<uint> codepoints, byte[] levels)
    {
        ResolveBrackets(seq, types, codepoints, levels);

        var idx = seq.Indices;
        var count = idx.Length;
        var embeddingDir = (levels[idx[0]] & 1) != 0 ? BidiClass.R : BidiClass.L;

        // N1: a sequence of neutrals/isolate-formatting between two strong types of the same
        // direction takes that direction (EN/AN count as R). N2: otherwise the embedding direction.
        var k = 0;
        while (k < count)
        {
            if (!IsNeutralOrIsolate(types[idx[k]])) { k++; continue; }
            var start = k;
            while (k < count && IsNeutralOrIsolate(types[idx[k]])) k++;
            var end = k; // exclusive

            var before = start > 0 ? StrongDir(types[idx[start - 1]]) : seq.Sos;
            var after = end < count ? StrongDir(types[idx[end]]) : seq.Eos;
            var resolved = before == after ? before : embeddingDir;
            for (var j = start; j < end; j++) types[idx[j]] = resolved;
        }
    }

    // BD16 + N0: match paired brackets on a stack (max 63 open) and resolve each pair's direction.
    private static void ResolveBrackets(RunSequence seq, BidiClass[] types, ReadOnlySpan<uint> codepoints, byte[] levels)
    {
        var idx = seq.Indices;
        var count = idx.Length;
        var embeddingDir = (levels[idx[0]] & 1) != 0 ? BidiClass.R : BidiClass.L;

        // Collect bracket pairs (opening seq-position, closing seq-position), sorted by opener.
        Span<int> openStackPos = stackalloc int[63];
        Span<uint> openStackPaired = stackalloc uint[63];
        var sp = 0;
        var pairs = new List<(int Open, int Close)>();
        for (var k = 0; k < count; k++)
        {
            if (types[idx[k]] != BidiClass.ON) continue; // only characters resolved to ON are brackets
            if (!BidiBrackets.TryGet(codepoints[idx[k]], out var paired, out var isOpen)) continue;
            if (isOpen)
            {
                if (sp == 63) break; // BD16: stack overflow → stop processing pairs
                openStackPaired[sp] = codepoints[idx[k]];
                openStackPos[sp] = k;
                sp++;
            }
            else
            {
                for (var s = sp - 1; s >= 0; s--)
                {
                    if (CanonicalMatch(openStackPaired[s], codepoints[idx[k]]))
                    {
                        pairs.Add((openStackPos[s], k));
                        sp = s; // pop this and everything above
                        break;
                    }
                }
            }
        }
        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));

        foreach (var (open, close) in pairs)
        {
            // N0: does a strong type matching the embedding direction appear between the brackets?
            var foundEmbedding = false;
            var foundOpposite = false;
            var oppositeDir = embeddingDir == BidiClass.L ? BidiClass.R : BidiClass.L;
            for (var k = open + 1; k < close; k++)
            {
                var d = StrongDir(types[idx[k]]);
                if (d == BidiClass.ON) continue;
                if (d == embeddingDir) { foundEmbedding = true; break; }
                foundOpposite = true;
            }

            BidiClass setDir;
            if (foundEmbedding) setDir = embeddingDir;                 // (b)
            else if (foundOpposite)
            {
                // (c): opposite direction inside — use it if the context before establishes it,
                // else the embedding direction.
                var priorDir = seq.Sos;
                for (var k = open - 1; k >= 0; k--)
                {
                    var d = StrongDir(types[idx[k]]);
                    if (d != BidiClass.ON) { priorDir = d; break; }
                }
                setDir = priorDir == oppositeDir ? oppositeDir : embeddingDir;
            }
            else continue; // (a) no strong type inside → leave as neutral for N1/N2

            types[idx[open]] = setDir;
            types[idx[close]] = setDir;
            // N0: a character that was ORIGINALLY an NSM and immediately follows either bracket (in
            // the sequence, so ignoring X9-removed chars) takes the bracket's resolved direction.
            // W1 has already rewritten these to ON, so test the original Bidi_Class, not the current.
            for (var k = open + 1; k < count && Bidi.Get(codepoints[idx[k]]) == BidiClass.NSM; k++) types[idx[k]] = setDir;
            for (var k = close + 1; k < count && Bidi.Get(codepoints[idx[k]]) == BidiClass.NSM; k++) types[idx[k]] = setDir;
        }
    }

    // Brackets match if their canonical codepoints correspond (handles the U+3009/U+232A style
    // canonical-equivalence pairs by comparing the opener's paired value with the closer).
    private static bool CanonicalMatch(uint opener, uint closer)
    {
        if (!BidiBrackets.TryGet(opener, out var openerPaired, out _)) return false;
        if (openerPaired == closer) return true;
        // Canonical equivalence: U+2329/U+232A ≡ U+3008/U+3009.
        static uint Canon(uint c) => c switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => c };
        return Canon(openerPaired) == Canon(closer);
    }

    private static bool IsNeutralOrIsolate(BidiClass t)
        => t is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON
             or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    // Strong direction for N-rule purposes: L→L, R/EN/AN→R (numbers act like R here), else ON.
    private static BidiClass StrongDir(BidiClass t) => t switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    // ---- I1–I2: implicit levels --------------------------------------------------------

    private static void ResolveImplicitLevels(RunSequence seq, BidiClass[] types, byte[] levels)
    {
        foreach (var i in seq.Indices)
        {
            var level = levels[i];
            var t = types[i];
            if ((level & 1) == 0) // even (L) embedding: I1
                levels[i] = t switch
                {
                    BidiClass.R => (byte)(level + 1),
                    BidiClass.AN or BidiClass.EN => (byte)(level + 2),
                    _ => level,
                };
            else // odd (R) embedding: I2
                levels[i] = t switch
                {
                    BidiClass.L or BidiClass.EN or BidiClass.AN => (byte)(level + 1),
                    _ => level,
                };
        }
    }

    // ---- L1 ----------------------------------------------------------------------------

    // Reset to the paragraph level: (1) segment separators, (2) paragraph separators, (3) any
    // sequence of whitespace / isolate formatting preceding a separator, and (4) any such trailing
    // sequence at the end of the line. Uses ORIGINAL types (L1 is defined on them).
    private static void ApplyL1(BidiClass[] original, byte[] levels, byte paraLevel)
    {
        var n = original.Length;
        var resetFrom = n; // start of the current trailing whitespace/isolate run
        for (var i = 0; i < n; i++)
        {
            var t = original[i];
            if (t is BidiClass.B or BidiClass.S)
            {
                levels[i] = paraLevel;
                for (var j = (resetFrom < i ? resetFrom : i); j < i; j++) levels[j] = paraLevel;
                resetFrom = n;
            }
            else if (t is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                       || IsRemovedByX9(t))
            {
                if (resetFrom == n) resetFrom = i;
            }
            else
            {
                resetFrom = n;
            }
        }
        for (var j = resetFrom; j < n; j++) levels[j] = paraLevel;
    }
}
