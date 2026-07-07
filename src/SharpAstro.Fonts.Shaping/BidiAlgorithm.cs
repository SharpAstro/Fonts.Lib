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

    // Per-thread reusable working buffers. Resolve needs several O(n) arrays per call and a text-heavy
    // caller reflows line by line, so pool them per thread (grown to the largest paragraph seen; the
    // live length is passed explicitly to each phase since the arrays may be oversized). The two
    // directional stacks are stackalloc'd instead. Net: zero steady-state allocation.
    private sealed class Scratch
    {
        public BidiClass[] Types = [];
        public BidiClass[] Original = [];
        public byte[] Levels = [];
        public int[] MatchingPDI = [];   // BD9: initiator -> matching PDI (or n)
        public int[] PdiStack = [];      // BD9 matching stack
        public int[] RunMembers = [];    // non-X9-removed indices grouped by level run
        public int[] RunStart = [];      // run r spans RunMembers[RunStart[r] .. RunStart[r]+RunLen[r])
        public int[] RunLen = [];
        public bool[] RunUsed = [];
        // Isolating run sequences partition the non-removed characters, so all their indices pack into
        // one buffer; sequence s occupies SeqAll[SeqStart[s] .. SeqStart[s]+SeqLen[s]) with sos/eos.
        public int[] SeqAll = [];
        public int[] SeqStart = [];
        public int[] SeqLen = [];
        public BidiClass[] SeqSos = [];
        public BidiClass[] SeqEos = [];

        public void EnsureCapacity(int n)
        {
            if (Types.Length >= n) return;
            var cap = Math.Max(n, 64);
            Types = new BidiClass[cap];
            Original = new BidiClass[cap];
            Levels = new byte[cap];
            MatchingPDI = new int[cap];
            PdiStack = new int[cap];
            RunMembers = new int[cap];
            RunStart = new int[cap];
            RunLen = new int[cap];
            RunUsed = new bool[cap];
            SeqAll = new int[cap];
            SeqStart = new int[cap];
            SeqLen = new int[cap];
            SeqSos = new BidiClass[cap];
            SeqEos = new BidiClass[cap];
        }
    }

    [ThreadStatic] private static Scratch? _scratch;

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

        var sc = _scratch ??= new Scratch();
        sc.EnsureCapacity(n);
        var types = sc.Types;
        var original = sc.Original;
        var levelArr = sc.Levels;
        for (var i = 0; i < n; i++)
        {
            var t = Bidi.Get(codepoints[i]);
            types[i] = t;
            original[i] = t;
        }

        // BD9: for each isolate initiator, the index of its matching PDI (or n if none).
        var matchingPDI = sc.MatchingPDI;
        ComputeMatchingPDI(types, n, matchingPDI, sc.PdiStack);

        var paraLevel = paragraphLevel is 0 or 1
            ? (byte)paragraphLevel
            : ComputeParagraphLevel(types, matchingPDI, 0, n);

        // X1–X8: explicit levels + override resolution, using the directional status stack.
        ResolveExplicit(types, matchingPDI, paraLevel, levelArr, n);

        // X9: remove explicit formatting characters (treat as BN for the rest of the algorithm).
        for (var i = 0; i < n; i++)
            if (IsRemovedByX9(original[i]))
                types[i] = BidiClass.BN;

        // X10: process each isolating run sequence (W, N, I rules run per sequence).
        ProcessIsolatingRunSequences(sc, types, original, levelArr, codepoints, paraLevel, matchingPDI, n);

        // L1: reset separators and trailing whitespace/isolates to the paragraph level.
        ApplyL1(original, levelArr, paraLevel, n);

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

    private static void ComputeMatchingPDI(BidiClass[] types, int n, int[] matching, int[] stack)
    {
        for (var i = 0; i < n; i++) matching[i] = n; // default: not an initiator / no matching PDI
        var sp = 0;
        for (var i = 0; i < n; i++)
        {
            switch (types[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    stack[sp++] = i;
                    break;
                case BidiClass.PDI when sp > 0:
                    matching[stack[--sp]] = i;
                    break;
            }
        }
    }

    // ---- X1–X8: explicit levels & directions -------------------------------------------

    private readonly record struct StatusEntry(byte Level, BidiClass Override, bool Isolate);

    private static void ResolveExplicit(BidiClass[] types, int[] matchingPDI, byte paraLevel, byte[] levels, int n)
    {
        // Directional status stack: at most MaxDepth+2 entries (UAX #9 X-rules), so stackalloc, no heap.
        Span<StatusEntry> stack = stackalloc StatusEntry[MaxDepth + 2];
        var sp = 0;
        stack[sp++] = new StatusEntry(paraLevel, BidiClass.ON, false); // ON = neutral override status

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
                    levels[i] = stack[sp - 1].Level; // X9 char; keep current level for now
                    var isRtl = t is BidiClass.RLE or BidiClass.RLO;
                    var newLevel = NextLevel(stack[sp - 1].Level, isRtl);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        var ov = t == BidiClass.LRO ? BidiClass.L : t == BidiClass.RLO ? BidiClass.R : BidiClass.ON;
                        stack[sp++] = new StatusEntry((byte)newLevel, ov, false);
                    }
                    else if (overflowIsolate == 0)
                    {
                        overflowEmbedding++;
                    }
                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    var cur = stack[sp - 1];
                    levels[i] = cur.Level;
                    if (cur.Override != BidiClass.ON) types[i] = cur.Override;

                    var isRtl = t == BidiClass.RLI
                        || (t == BidiClass.FSI && ComputeParagraphLevel(types, matchingPDI, i + 1, matchingPDI[i]) == 1);
                    var newLevel = NextLevel(cur.Level, isRtl);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        validIsolate++;
                        stack[sp++] = new StatusEntry((byte)newLevel, BidiClass.ON, true);
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
                        while (!stack[sp - 1].Isolate) sp--;
                        sp--;
                        validIsolate--;
                    }
                    var cur = stack[sp - 1];
                    levels[i] = cur.Level;
                    if (cur.Override != BidiClass.ON) types[i] = cur.Override;
                    break;
                }
                case BidiClass.PDF:
                {
                    levels[i] = stack[sp - 1].Level;
                    if (overflowIsolate > 0) { /* nothing */ }
                    else if (overflowEmbedding > 0) overflowEmbedding--;
                    else if (!stack[sp - 1].Isolate && sp >= 2) sp--;
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
                    levels[i] = stack[sp - 1].Level;
                    break;
                }
                default:
                {
                    var cur = stack[sp - 1];
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

    /// <summary>
    /// Build the isolating run sequences (X10 / BD13) and run the per-sequence W, N and I rules on
    /// each in turn. Level runs (maximal equal-level runs over the non-X9-removed characters) are
    /// flattened into the scratch <c>RunMembers</c> buffer and chained through isolate initiators to
    /// their matching PDI's run; each sequence's indices are gathered into the scratch <c>SeqBuf</c>
    /// and resolved in place. No per-sequence heap allocation (was List&lt;List&lt;int&gt;&gt; +
    /// Dictionary + a RunSequence object and index array per sequence).
    /// </summary>
    private static void ProcessIsolatingRunSequences(Scratch sc, BidiClass[] types, BidiClass[] original,
        byte[] levels, ReadOnlySpan<uint> codepoints, byte paraLevel, int[] matchingPDI, int n)
    {
        // Level runs: RunMembers holds the non-X9-removed indices in order, grouped by run; run r
        // occupies RunMembers[RunStart[r] .. RunStart[r]+RunLen[r]).
        var members = sc.RunMembers;
        var runStart = sc.RunStart;
        var runLen = sc.RunLen;
        var runCount = 0;
        var m = 0;
        var currentLevel = -1;
        for (var i = 0; i < n; i++)
        {
            if (IsRemovedByX9(original[i])) continue;
            if (runCount == 0 || levels[i] != currentLevel)
            {
                if (runCount > 0) runLen[runCount - 1] = m - runStart[runCount - 1];
                runStart[runCount++] = m;
                currentLevel = levels[i];
            }
            members[m++] = i;
        }
        if (runCount > 0) runLen[runCount - 1] = m - runStart[runCount - 1];

        var used = sc.RunUsed;
        for (var r = 0; r < runCount; r++) used[r] = false;

        // Phase 1: build every isolating run sequence (chaining runs through isolate initiators, BD13)
        // and capture its sos/eos NOW — while `levels` still holds embedding levels. ResolveImplicit
        // in phase 2 mutates `levels`, so every sos/eos must be taken before any sequence is processed
        // (a boundary scan for one sequence reads a neighbour's otherwise-mutated level).
        var seqAll = sc.SeqAll;
        var seqStart = sc.SeqStart;
        var seqLen = sc.SeqLen;
        var seqSos = sc.SeqSos;
        var seqEos = sc.SeqEos;
        var seqCount = 0;
        var allLen = 0;
        for (var r = 0; r < runCount; r++)
        {
            if (used[r]) continue;
            // A sequence starts at a run whose first char is not a PDI matching an isolate initiator (BD13).
            var firstIdx = members[runStart[r]];
            if (original[firstIdx] == BidiClass.PDI && HasMatchingInitiator(matchingPDI, n, firstIdx))
                continue;

            var start = allLen;
            var cur = r;
            while (true)
            {
                used[cur] = true;
                var rs = runStart[cur];
                var rl = runLen[cur];
                for (var k = 0; k < rl; k++) seqAll[allLen++] = members[rs + k];
                var last = members[rs + rl - 1];
                if (original[last] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI
                    && matchingPDI[last] < n)
                {
                    var nextRun = FindRunStartingAt(members, runStart, runCount, matchingPDI[last]);
                    if (nextRun >= 0) { cur = nextRun; continue; }
                }
                break;
            }

            seqStart[seqCount] = start;
            seqLen[seqCount] = allLen - start;
            var (sos, eos) = ComputeSosEos(seqAll.AsSpan(start, allLen - start), types, levels, paraLevel, n);
            seqSos[seqCount] = sos;
            seqEos[seqCount] = eos;
            seqCount++;
        }

        // Phase 2: resolve weak (W1-W7), neutral (N0-N2) and implicit (I1-I2) types per sequence.
        for (var s = 0; s < seqCount; s++)
        {
            var idx = seqAll.AsSpan(seqStart[s], seqLen[s]);
            ResolveWeakTypes(idx, seqSos[s], types);
            ResolveNeutralTypes(idx, seqSos[s], seqEos[s], types, codepoints, levels);
            ResolveImplicitLevels(idx, types, levels);
        }
    }

    // Which level run begins at original index `firstIndex`; -1 if none. Runs are in ascending order
    // of their first index, so binary search (replaces the old first-index -> run-number dictionary).
    private static int FindRunStartingAt(int[] members, int[] runStart, int runCount, int firstIndex)
    {
        int lo = 0, hi = runCount - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var f = members[runStart[mid]];
            if (firstIndex < f) hi = mid - 1;
            else if (firstIndex > f) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private static bool HasMatchingInitiator(int[] matchingPDI, int n, int pdiIndex)
    {
        for (var i = 0; i < n; i++)
            if (matchingPDI[i] == pdiIndex)
                return true;
        return false;
    }

    // sos/eos (X10): compare the sequence's boundary level with the adjacent character's level
    // (or the paragraph level at the text edges); the higher level's parity gives L or R.
    private static (BidiClass Sos, BidiClass Eos) ComputeSosEos(
        ReadOnlySpan<int> idx, BidiClass[] types, byte[] levels, byte paraLevel, int n)
    {
        var first = idx[0];
        var last = idx[^1];
        var seqLevel = levels[first];

        var prevLevel = paraLevel;
        for (var i = first - 1; i >= 0; i--)
        {
            if (IsRemovedByX9Level(types, i)) continue;
            prevLevel = levels[i];
            break;
        }
        var sos = ((Math.Max(seqLevel, prevLevel) & 1) != 0) ? BidiClass.R : BidiClass.L;

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
        var eos = ((Math.Max(endLevel, nextLevel) & 1) != 0) ? BidiClass.R : BidiClass.L;
        return (sos, eos);
    }

    private static bool IsRemovedByX9Level(BidiClass[] types, int i) => types[i] == BidiClass.BN;

    // ---- W1–W7 -------------------------------------------------------------------------

    private static void ResolveWeakTypes(ReadOnlySpan<int> idx, BidiClass sos, BidiClass[] types)
    {
        var count = idx.Length;

        // W1: NSM → type of previous character in the sequence (sos at the start); isolate
        // initiators and PDI count as ON for this purpose.
        var prev = sos;
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
        var lastStrong = sos;
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
        lastStrong = sos;
        for (var k = 0; k < count; k++)
        {
            var t = types[idx[k]];
            if (t == BidiClass.EN && lastStrong == BidiClass.L) types[idx[k]] = BidiClass.L;
            if (t is BidiClass.L or BidiClass.R) lastStrong = t;
        }
    }

    // ---- N0: paired brackets -----------------------------------------------------------

    private static void ResolveNeutralTypes(ReadOnlySpan<int> idx, BidiClass sos, BidiClass eos,
        BidiClass[] types, ReadOnlySpan<uint> codepoints, byte[] levels)
    {
        ResolveBrackets(idx, sos, types, codepoints, levels);

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

            var before = start > 0 ? StrongDir(types[idx[start - 1]]) : sos;
            var after = end < count ? StrongDir(types[idx[end]]) : eos;
            var resolved = before == after ? before : embeddingDir;
            for (var j = start; j < end; j++) types[idx[j]] = resolved;
        }
    }

    // A matched bracket pair: opening and closing positions within the sequence's index list.
    private readonly record struct BracketPair(int Open, int Close);

    // BD16 + N0: match paired brackets on a stack (max 63 open) and resolve each pair's direction.
    private static void ResolveBrackets(ReadOnlySpan<int> idx, BidiClass sos,
        BidiClass[] types, ReadOnlySpan<uint> codepoints, byte[] levels)
    {
        var count = idx.Length;
        var embeddingDir = (levels[idx[0]] & 1) != 0 ? BidiClass.R : BidiClass.L;

        // Collect bracket pairs (opening seq-position, closing seq-position), sorted by opener.
        Span<int> openStackPos = stackalloc int[63];
        Span<uint> openStackPaired = stackalloc uint[63];
        var sp = 0;
        Span<BracketPair> pairs = stackalloc BracketPair[63];
        var pairCount = 0;
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
                        pairs[pairCount++] = new BracketPair(openStackPos[s], k);
                        sp = s; // pop this and everything above
                        break;
                    }
                }
            }
        }
        pairs[..pairCount].Sort(static (a, b) => a.Open.CompareTo(b.Open));

        foreach (var (open, close) in pairs[..pairCount])
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
                var priorDir = sos;
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

    private static void ResolveImplicitLevels(ReadOnlySpan<int> idx, BidiClass[] types, byte[] levels)
    {
        foreach (var i in idx)
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
    private static void ApplyL1(BidiClass[] original, byte[] levels, byte paraLevel, int n)
    {
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
