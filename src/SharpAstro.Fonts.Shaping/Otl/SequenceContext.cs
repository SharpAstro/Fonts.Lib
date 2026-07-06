namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// Contextual lookups: GSUB 5 / GPOS 7 (context) and GSUB 6 / GPOS 8 (chained context).
/// The subtable layout is identical between GSUB and GPOS — only the lookup list the
/// seqLookupRecords index into differs, and the <see cref="LookupRunner"/> already knows
/// which table it's driving — so both share this matcher. A subtable matches a glyph
/// sequence (optionally with backtrack/lookahead context, all skip-aware) and then invokes
/// nested lookups at chosen positions via <see cref="LookupRunner.ApplyNested"/>.
///
/// <para>Three formats each: 1 = glyph sequences per coverage-indexed rule set, 2 = class
/// sequences per class-indexed rule set, 3 = one inline coverage array. The collectors
/// below turn "the next N non-skipped glyphs forward/backward" into positions; each format
/// then checks those positions by glyph id (fmt 1), class (fmt 2), or coverage (fmt 3).</para>
///
/// <para>Spec: OpenType Layout Common Table Formats (Sequence Context / Chained Sequence
/// Context). https://learn.microsoft.com/typography/opentype/spec/chapter2</para>
/// </summary>
internal static class SequenceContext
{
    // Backtrack/input/lookahead sequences longer than this are ignored (real contexts are a
    // handful of glyphs); the cap bounds the stack buffers of collected positions.
    private const int MaxSeq = 64;

    // ---- Context (GSUB 5 / GPOS 7) ----------------------------------------------------

    public static bool ApplyContext(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
        => (subtable.Length >= 2 ? ReadU16(subtable, 0) : 0) switch
        {
            1 => ContextFormat1(runner, lookup, subtable, buffer, ref i, depth),
            2 => ContextFormat2(runner, lookup, subtable, buffer, ref i, depth),
            3 => ContextFormat3(runner, lookup, subtable, buffer, ref i, depth),
            _ => false,
        };

    private static bool ContextFormat1(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        if (subtable.Length < 6) return false;
        var covIdx = Coverage.Parse(subtable, ReadU16(subtable, 2)).GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (covIdx < 0) return false;
        var ruleSetCount = ReadU16(subtable, 4);
        if (covIdx >= ruleSetCount) return false;
        return TryRuleSet(runner, lookup, subtable, ReadU16(subtable, 6 + covIdx * 2),
            buffer, ref i, depth, inputClasses: null);
    }

    private static bool ContextFormat2(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        if (subtable.Length < 8) return false;
        if (!Coverage.Parse(subtable, ReadU16(subtable, 2)).Contains(buffer.GlyphsMutable[i])) return false;
        var classDef = ClassDef.Parse(subtable, ReadU16(subtable, 4));
        var cls = classDef.GetClass(buffer.GlyphsMutable[i]);
        var ruleSetCount = ReadU16(subtable, 6);
        if (cls >= ruleSetCount) return false;
        return TryRuleSet(runner, lookup, subtable, ReadU16(subtable, 8 + cls * 2),
            buffer, ref i, depth, inputClasses: classDef);
    }

    private static bool ContextFormat3(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        if (subtable.Length < 6) return false;
        var glyphCount = ReadU16(subtable, 2);
        var recordCount = ReadU16(subtable, 4);
        if (glyphCount is 0 or > MaxSeq) return false;
        var covArrayPos = 6;
        var recordsPos = covArrayPos + glyphCount * 2;
        if (recordsPos + recordCount * 4 > subtable.Length) return false;

        Span<int> inputPos = stackalloc int[MaxSeq];
        if (!MatchCoverageInput(runner, lookup, subtable, covArrayPos, glyphCount, buffer, i, inputPos))
            return false;

        var delta = ApplyLookupRecords(runner, subtable[recordsPos..], recordCount, inputPos[..glyphCount], buffer, depth);
        i = inputPos[glyphCount - 1] + 1 + delta;
        return true;
    }

    // ---- Chained context (GSUB 6 / GPOS 8) --------------------------------------------

    public static bool ApplyChainedContext(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
        => (subtable.Length >= 2 ? ReadU16(subtable, 0) : 0) switch
        {
            1 => ChainedFormat1(runner, lookup, subtable, buffer, ref i, depth),
            2 => ChainedFormat2(runner, lookup, subtable, buffer, ref i, depth),
            3 => ChainedFormat3(runner, lookup, subtable, buffer, ref i, depth),
            _ => false,
        };

    private static bool ChainedFormat1(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        if (subtable.Length < 6) return false;
        var covIdx = Coverage.Parse(subtable, ReadU16(subtable, 2)).GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (covIdx < 0) return false;
        var ruleSetCount = ReadU16(subtable, 4);
        if (covIdx >= ruleSetCount) return false;
        return TryChainedRuleSet(runner, lookup, subtable, ReadU16(subtable, 6 + covIdx * 2),
            buffer, ref i, depth, null, null, null);
    }

    private static bool ChainedFormat2(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        if (subtable.Length < 12) return false;
        if (!Coverage.Parse(subtable, ReadU16(subtable, 2)).Contains(buffer.GlyphsMutable[i])) return false;
        var backtrackClasses = ClassDef.Parse(subtable, ReadU16(subtable, 4));
        var inputClasses = ClassDef.Parse(subtable, ReadU16(subtable, 6));
        var lookaheadClasses = ClassDef.Parse(subtable, ReadU16(subtable, 8));
        var cls = inputClasses.GetClass(buffer.GlyphsMutable[i]);
        var ruleSetCount = ReadU16(subtable, 10);
        if (cls >= ruleSetCount) return false;
        return TryChainedRuleSet(runner, lookup, subtable, ReadU16(subtable, 12 + cls * 2),
            buffer, ref i, depth, backtrackClasses, inputClasses, lookaheadClasses);
    }

    private static bool ChainedFormat3(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        var font = runner.Font;
        var pos = 2;
        if (!ReadCount(subtable, ref pos, out var backtrackCount)) return false;
        var backtrackCovPos = pos;
        pos += backtrackCount * 2;
        if (!ReadCount(subtable, ref pos, out var inputCount) || inputCount is 0 or > MaxSeq) return false;
        var inputCovPos = pos;
        pos += inputCount * 2;
        if (!ReadCount(subtable, ref pos, out var lookaheadCount)) return false;
        var lookaheadCovPos = pos;
        pos += lookaheadCount * 2;
        if (!ReadCount(subtable, ref pos, out var recordCount)) return false;
        var recordsPos = pos;
        if (backtrackCount > MaxSeq || lookaheadCount > MaxSeq) return false;
        if (recordsPos + recordCount * 4 > subtable.Length) return false;

        Span<int> inputPos = stackalloc int[MaxSeq];
        if (!MatchCoverageInput(runner, lookup, subtable, inputCovPos, inputCount, buffer, i, inputPos))
            return false;

        Span<int> backPos = stackalloc int[MaxSeq];
        if (!CollectBackward(font, lookup, buffer, i, backtrackCount, backPos)) return false;
        for (var k = 0; k < backtrackCount; k++)
            if (!Coverage.Parse(subtable, ReadU16(subtable, backtrackCovPos + k * 2)).Contains(buffer.GlyphsMutable[backPos[k]]))
                return false;

        Span<int> aheadPos = stackalloc int[MaxSeq];
        if (!CollectForward(font, lookup, buffer, inputPos[inputCount - 1], lookaheadCount, aheadPos)) return false;
        for (var k = 0; k < lookaheadCount; k++)
            if (!Coverage.Parse(subtable, ReadU16(subtable, lookaheadCovPos + k * 2)).Contains(buffer.GlyphsMutable[aheadPos[k]]))
                return false;

        var delta = ApplyLookupRecords(runner, subtable[recordsPos..], recordCount, inputPos[..inputCount], buffer, depth);
        i = inputPos[inputCount - 1] + 1 + delta;
        return true;
    }

    // ---- rule-set iteration (formats 1/2) ---------------------------------------------

    private static bool TryRuleSet(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        int ruleSetOffset, ShapeBuffer buffer, ref int i, int depth, ClassDef? inputClasses)
    {
        if (ruleSetOffset == 0 || ruleSetOffset + 2 > subtable.Length) return false;
        var ruleSet = subtable[ruleSetOffset..];
        var ruleCount = ReadU16(ruleSet, 0);
        for (var r = 0; r < ruleCount; r++)
        {
            if (2 + r * 2 + 2 > ruleSet.Length) break;
            var ruleOffset = ReadU16(ruleSet, 2 + r * 2);
            if (ruleOffset == 0 || ruleSetOffset + ruleOffset >= subtable.Length) continue;
            if (TryContextRule(runner, lookup, subtable[(ruleSetOffset + ruleOffset)..], buffer, ref i, depth, inputClasses))
                return true;
        }
        return false;
    }

    // SequenceRule / ClassSequenceRule: glyphCount(2), seqLookupCount(2),
    // inputSequence[glyphCount-1] (2 each), seqLookupRecords[seqLookupCount] (4 each).
    private static bool TryContextRule(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> rule,
        ShapeBuffer buffer, ref int i, int depth, ClassDef? inputClasses)
    {
        if (rule.Length < 4) return false;
        var glyphCount = ReadU16(rule, 0);
        var recordCount = ReadU16(rule, 2);
        if (glyphCount is 0 or > MaxSeq) return false;
        var inputValsPos = 4;
        var recordsPos = inputValsPos + (glyphCount - 1) * 2;
        if (recordsPos + recordCount * 4 > rule.Length) return false;

        Span<int> inputPos = stackalloc int[MaxSeq];
        inputPos[0] = i;
        if (!CollectForward(runner.Font, lookup, buffer, i, glyphCount - 1, inputPos[1..])) return false;
        for (var k = 1; k < glyphCount; k++)
        {
            var expected = ReadU16(rule, inputValsPos + (k - 1) * 2);
            if (!MatchValue(inputClasses, buffer.GlyphsMutable[inputPos[k]], expected)) return false;
        }

        var delta = ApplyLookupRecords(runner, rule[recordsPos..], recordCount, inputPos[..glyphCount], buffer, depth);
        i = inputPos[glyphCount - 1] + 1 + delta;
        return true;
    }

    private static bool TryChainedRuleSet(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        int ruleSetOffset, ShapeBuffer buffer, ref int i, int depth,
        ClassDef? backtrackClasses, ClassDef? inputClasses, ClassDef? lookaheadClasses)
    {
        if (ruleSetOffset == 0 || ruleSetOffset + 2 > subtable.Length) return false;
        var ruleSet = subtable[ruleSetOffset..];
        var ruleCount = ReadU16(ruleSet, 0);
        for (var r = 0; r < ruleCount; r++)
        {
            if (2 + r * 2 + 2 > ruleSet.Length) break;
            var ruleOffset = ReadU16(ruleSet, 2 + r * 2);
            if (ruleOffset == 0 || ruleSetOffset + ruleOffset >= subtable.Length) continue;
            if (TryChainedRule(runner, lookup, subtable[(ruleSetOffset + ruleOffset)..],
                    buffer, ref i, depth, backtrackClasses, inputClasses, lookaheadClasses))
                return true;
        }
        return false;
    }

    // ChainedSequenceRule / ChainedClassSequenceRule: backtrackCount(2), backtrack[] (2 each),
    // inputCount(2), input[inputCount-1] (2 each), lookaheadCount(2), lookahead[] (2 each),
    // seqLookupCount(2), records[] (4 each). Backtrack is stored nearest-first.
    private static bool TryChainedRule(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> rule,
        ShapeBuffer buffer, ref int i, int depth,
        ClassDef? backtrackClasses, ClassDef? inputClasses, ClassDef? lookaheadClasses)
    {
        var font = runner.Font;
        var pos = 0;
        if (!ReadCount(rule, ref pos, out var backtrackCount) || backtrackCount > MaxSeq) return false;
        var backtrackValsPos = pos;
        pos += backtrackCount * 2;
        if (!ReadCount(rule, ref pos, out var inputCount) || inputCount is 0 or > MaxSeq) return false;
        var inputValsPos = pos;
        pos += (inputCount - 1) * 2;
        if (!ReadCount(rule, ref pos, out var lookaheadCount) || lookaheadCount > MaxSeq) return false;
        var lookaheadValsPos = pos;
        pos += lookaheadCount * 2;
        if (!ReadCount(rule, ref pos, out var recordCount)) return false;
        var recordsPos = pos;
        if (recordsPos + recordCount * 4 > rule.Length) return false;

        Span<int> inputPos = stackalloc int[MaxSeq];
        inputPos[0] = i;
        if (!CollectForward(font, lookup, buffer, i, inputCount - 1, inputPos[1..])) return false;
        for (var k = 1; k < inputCount; k++)
            if (!MatchValue(inputClasses, buffer.GlyphsMutable[inputPos[k]], ReadU16(rule, inputValsPos + (k - 1) * 2)))
                return false;

        Span<int> backPos = stackalloc int[MaxSeq];
        if (!CollectBackward(font, lookup, buffer, i, backtrackCount, backPos)) return false;
        for (var k = 0; k < backtrackCount; k++)
            if (!MatchValue(backtrackClasses, buffer.GlyphsMutable[backPos[k]], ReadU16(rule, backtrackValsPos + k * 2)))
                return false;

        Span<int> aheadPos = stackalloc int[MaxSeq];
        if (!CollectForward(font, lookup, buffer, inputPos[inputCount - 1], lookaheadCount, aheadPos)) return false;
        for (var k = 0; k < lookaheadCount; k++)
            if (!MatchValue(lookaheadClasses, buffer.GlyphsMutable[aheadPos[k]], ReadU16(rule, lookaheadValsPos + k * 2)))
                return false;

        var delta = ApplyLookupRecords(runner, rule[recordsPos..], recordCount, inputPos[..inputCount], buffer, depth);
        i = inputPos[inputCount - 1] + 1 + delta;
        return true;
    }

    // ---- shared helpers ---------------------------------------------------------------

    /// <summary>
    /// Match the input coverage array (format 3): input[0] is the current glyph at
    /// <paramref name="from"/>, the rest are collected forward (skip-aware). Fills
    /// <paramref name="inputPos"/>[0..count) with the matched buffer positions.
    /// </summary>
    private static bool MatchCoverageInput(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        int covArrayPos, int count, ShapeBuffer buffer, int from, Span<int> inputPos)
    {
        inputPos[0] = from;
        if (!Coverage.Parse(subtable, ReadU16(subtable, covArrayPos)).Contains(buffer.GlyphsMutable[from]))
            return false;
        if (!CollectForward(runner.Font, lookup, buffer, from, count - 1, inputPos[1..])) return false;
        for (var k = 1; k < count; k++)
            if (!Coverage.Parse(subtable, ReadU16(subtable, covArrayPos + k * 2)).Contains(buffer.GlyphsMutable[inputPos[k]]))
                return false;
        return true;
    }

    /// <summary>Collect the next <paramref name="count"/> non-skipped positions strictly after
    /// <paramref name="from"/> (forward). <paramref name="positions"/>[0] is nearest to <paramref name="from"/>.</summary>
    internal static bool CollectForward(ShapingFont font, Lookup lookup, ShapeBuffer buffer,
        int from, int count, Span<int> positions)
    {
        var pos = from;
        for (var k = 0; k < count; k++)
        {
            pos = GlyphIterator.Next(buffer, font.Gdef, lookup.Flags, lookup.MarkFilteringSet, pos);
            if (pos < 0) return false;
            positions[k] = pos;
        }
        return true;
    }

    /// <summary>Collect the next <paramref name="count"/> non-skipped positions strictly before
    /// <paramref name="from"/> (backward). <paramref name="positions"/>[0] is nearest to <paramref name="from"/>.</summary>
    internal static bool CollectBackward(ShapingFont font, Lookup lookup, ShapeBuffer buffer,
        int from, int count, Span<int> positions)
    {
        var pos = from;
        for (var k = 0; k < count; k++)
        {
            pos = GlyphIterator.Prev(buffer, font.Gdef, lookup.Flags, lookup.MarkFilteringSet, pos);
            if (pos < 0) return false;
            positions[k] = pos;
        }
        return true;
    }

    /// <summary>Compare a buffer glyph against a rule value: by glyph id (fmt 1, <paramref name="classes"/>
    /// null) or by class (fmt 2).</summary>
    private static bool MatchValue(ClassDef? classes, uint glyph, ushort expected)
        => classes is null ? glyph == expected : classes.GetClass(glyph) == expected;

    /// <summary>
    /// Apply the subtable's seqLookupRecords: each (sequenceIndex, lookupListIndex) runs the
    /// referenced lookup at the input glyph with that sequence index. Returns the net buffer
    /// length change so the caller can advance past the (possibly resized) matched input.
    /// A cumulative delta shifts later positions when an earlier nested lookup grows/shrinks
    /// the run (correct for records applied in sequence-index order — the usual case).
    /// </summary>
    private static int ApplyLookupRecords(LookupRunner runner, ReadOnlySpan<byte> records,
        int recordCount, ReadOnlySpan<int> inputPositions, ShapeBuffer buffer, int depth)
    {
        var totalDelta = 0;
        for (var r = 0; r < recordCount; r++)
        {
            var seqIndex = ReadU16(records, r * 4);
            var lookupIndex = ReadU16(records, r * 4 + 2);
            if (seqIndex >= inputPositions.Length) continue;
            var pos = inputPositions[seqIndex] + totalDelta;
            if ((uint)pos >= (uint)buffer.Length) continue;
            var before = buffer.Length;
            runner.ApplyNested(lookupIndex, buffer, pos, depth);
            totalDelta += buffer.Length - before;
        }
        return totalDelta;
    }

    /// <summary>Read a uint16 count at <paramref name="pos"/> and advance past it; false if truncated.</summary>
    private static bool ReadCount(ReadOnlySpan<byte> data, ref int pos, out int count)
    {
        if (pos + 2 > data.Length) { count = 0; return false; }
        count = ReadU16(data, pos);
        pos += 2;
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
