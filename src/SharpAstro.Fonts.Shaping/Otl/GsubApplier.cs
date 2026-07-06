using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GSUB substitution appliers. H1 built type 1 (single) and 4 (ligature); H2 added 2
/// (multiple) and 3 (alternate); H3 adds 5/6 (context / chained context, via
/// <see cref="SequenceContext"/>) and 8 (reverse chaining, applied back-to-front by the
/// runner). Each tries to apply one subtable at buffer position <c>i</c>; on success it
/// mutates the buffer and advances <c>i</c> past the output, returning true. Substituted
/// glyphs re-derive their GDEF class from the font so downstream mark processing sees the
/// right classes (e.g. a <c>ccmp</c> that maps a codepoint to a mark glyph).
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/gsub</para>
/// </summary>
internal static class GsubApplier
{
    // Ligatures with more components than this are ignored (real ligatures are 2–4;
    // the cap bounds the stack buffer for matched component indices).
    private const int MaxComponents = 16;

    // Multiple-substitution sequences longer than this are skipped (real decompositions
    // are 2–4 glyphs; the cap bounds the stack buffers for the output glyphs/classes).
    private const int MaxSequence = 32;

    // Backtrack/lookahead sequences longer than this are ignored (bounds the stack buffers).
    private const int MaxContext = 64;

    public static bool Apply(LookupRunner runner, Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapeBuffer buffer, ref int i, int depth)
    {
        var font = runner.Font;
        return lookup.Type switch
        {
            1 => ApplySingle(subtable, font, buffer, ref i),
            2 => ApplyMultiple(subtable, font, buffer, ref i),
            3 => ApplyAlternate(subtable, font, buffer, ref i),
            4 => ApplyLigature(lookup, subtable, font, buffer, ref i),
            5 => SequenceContext.ApplyContext(runner, lookup, subtable, buffer, ref i, depth),
            6 => SequenceContext.ApplyChainedContext(runner, lookup, subtable, buffer, ref i, depth),
            _ => false, // 8 (reverse chaining) is applied back-to-front via ApplyReverseChain
        };
    }

    /// <summary>
    /// Type 8 — Reverse Chaining Contextual Single Substitution, applied at a fixed position
    /// by the runner's back-to-front pass (no <c>ref i</c> — the reverse loop steps the index).
    /// Format 1: the current glyph must be covered and the backtrack/lookahead coverage
    /// sequences must match (skip-aware); on success the glyph is replaced in place
    /// (1→1) by <c>substituteGlyphIDs[coverageIndex]</c>. It has no seqLookupRecords —
    /// unlike the forward context types, it substitutes directly.
    /// </summary>
    public static bool ApplyReverseChain(LookupRunner runner, Lookup lookup,
        ReadOnlySpan<byte> subtable, ShapeBuffer buffer, int i)
    {
        if (subtable.Length < 6 || ReadU16(subtable, 0) != 1) return false;
        var font = runner.Font;
        var covIdx = Coverage.IndexOf(subtable, ReadU16(subtable, 2), buffer.GlyphsMutable[i]);
        if (covIdx < 0) return false;

        var pos = 4;
        var backtrackCount = ReadU16(subtable, pos);
        pos += 2;
        var backtrackCovPos = pos;
        pos += backtrackCount * 2;
        if (pos + 2 > subtable.Length) return false;
        var lookaheadCount = ReadU16(subtable, pos);
        pos += 2;
        var lookaheadCovPos = pos;
        pos += lookaheadCount * 2;
        if (pos + 2 > subtable.Length) return false;
        var glyphCount = ReadU16(subtable, pos);
        pos += 2;
        var substitutesPos = pos;
        if (covIdx >= glyphCount || substitutesPos + glyphCount * 2 > subtable.Length) return false;
        if (backtrackCount > MaxContext || lookaheadCount > MaxContext) return false;

        Span<int> backPos = stackalloc int[MaxContext];
        if (!SequenceContext.CollectBackward(font, lookup, buffer, i, backtrackCount, backPos)) return false;
        for (var k = 0; k < backtrackCount; k++)
            if (!Coverage.Covers(subtable, ReadU16(subtable, backtrackCovPos + k * 2), buffer.GlyphsMutable[backPos[k]]))
                return false;

        Span<int> aheadPos = stackalloc int[MaxContext];
        if (!SequenceContext.CollectForward(font, lookup, buffer, i, lookaheadCount, aheadPos)) return false;
        for (var k = 0; k < lookaheadCount; k++)
            if (!Coverage.Covers(subtable, ReadU16(subtable, lookaheadCovPos + k * 2), buffer.GlyphsMutable[aheadPos[k]]))
                return false;

        var newGid = ReadU16(subtable, substitutesPos + covIdx * 2);
        buffer.Substitute(i, newGid, font.Gdef.GetGlyphClass(newGid));
        return true;
    }

    private static bool ApplySingle(ReadOnlySpan<byte> subtable, ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        var coverageOffset = r.ReadUInt16();
        var gid = buffer.GlyphsMutable[i];
        var covIdx = Coverage.IndexOf(subtable, coverageOffset, gid);
        if (covIdx < 0) return false;

        if (format == 1)
        {
            var delta = r.ReadInt16();
            var newGid = (uint)((gid + delta) & 0xFFFF);
            buffer.Substitute(i, newGid, font.Gdef.GetGlyphClass(newGid));
            i++;
            return true;
        }

        if (format == 2)
        {
            var glyphCount = r.ReadUInt16();
            if (covIdx >= glyphCount) return false;
            // substituteGlyphIDs[] follows glyphCount; each is a uint16.
            r.Skip(covIdx * 2);
            if (r.Remaining < 2) return false;
            var newGid = r.ReadUInt16();
            buffer.Substitute(i, newGid, font.Gdef.GetGlyphClass(newGid));
            i++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Type 2 — one glyph expands to a sequence (e.g. a <c>ccmp</c> decomposition). The
    /// output glyphs share the input's cluster; <c>i</c> advances past them all. A
    /// zero-length sequence deletes the glyph (<c>i</c> stays, now pointing at the glyph
    /// that shifted into place — HarfBuzz's behavior).
    /// </summary>
    private static bool ApplyMultiple(ReadOnlySpan<byte> subtable, ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        if (format != 1) return false;
        var coverageOffset = r.ReadUInt16();
        var sequenceCount = r.ReadUInt16();

        var covIdx = Coverage.IndexOf(subtable, coverageOffset, buffer.GlyphsMutable[i]);
        if (covIdx < 0 || covIdx >= sequenceCount) return false;

        var seqOffsetPos = 6 + covIdx * 2;
        if (seqOffsetPos + 2 > subtable.Length) return false;
        var seqOffset = ReadU16(subtable, seqOffsetPos);
        if (seqOffset == 0 || seqOffset + 2 > subtable.Length) return false;

        var seq = subtable[seqOffset..];
        var glyphCount = ReadU16(seq, 0);
        if (glyphCount > MaxSequence) return false;
        if (2 + glyphCount * 2 > seq.Length) return false;

        Span<uint> outGlyphs = stackalloc uint[glyphCount];
        Span<byte> outClasses = stackalloc byte[glyphCount];
        for (var g = 0; g < glyphCount; g++)
        {
            var gid = ReadU16(seq, 2 + g * 2);
            outGlyphs[g] = gid;
            outClasses[g] = (byte)font.Gdef.GetGlyphClass(gid);
        }

        buffer.ReplaceWithSequence(i, outGlyphs, outClasses);
        i += glyphCount; // deletion (glyphCount 0) leaves i on the shifted-in glyph
        return true;
    }

    /// <summary>
    /// Type 3 — replace a glyph with one of its alternates. Our feature model is on/off
    /// (no per-feature alternate-selection value — that's <c>aalt</c>/<c>ss01</c>+/<c>cv01</c>+,
    /// features outside the default plan), so we take the first alternate, which is what
    /// HarfBuzz picks for a feature enabled with value 1.
    /// </summary>
    private static bool ApplyAlternate(ReadOnlySpan<byte> subtable, ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        if (format != 1) return false;
        var coverageOffset = r.ReadUInt16();
        var alternateSetCount = r.ReadUInt16();

        var covIdx = Coverage.IndexOf(subtable, coverageOffset, buffer.GlyphsMutable[i]);
        if (covIdx < 0 || covIdx >= alternateSetCount) return false;

        var setOffsetPos = 6 + covIdx * 2;
        if (setOffsetPos + 2 > subtable.Length) return false;
        var setOffset = ReadU16(subtable, setOffsetPos);
        if (setOffset == 0 || setOffset + 2 > subtable.Length) return false;

        var set = subtable[setOffset..];
        var glyphCount = ReadU16(set, 0);
        if (glyphCount == 0 || 2 + 2 > set.Length) return false;

        var newGid = ReadU16(set, 2); // alternates[0]
        buffer.Substitute(i, newGid, font.Gdef.GetGlyphClass(newGid));
        i++;
        return true;
    }

    private static bool ApplyLigature(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        if (format != 1) return false;
        var coverageOffset = r.ReadUInt16();
        var ligatureSetCount = r.ReadUInt16();

        var covIdx = Coverage.IndexOf(subtable, coverageOffset, buffer.GlyphsMutable[i]);
        if (covIdx < 0 || covIdx >= ligatureSetCount) return false;

        // ligatureSetOffsets[covIdx] (relative to subtable start).
        var setOffsetPos = 6 + covIdx * 2;
        if (setOffsetPos + 2 > subtable.Length) return false;
        var ligatureSetOffset = ReadU16(subtable, setOffsetPos);
        if (ligatureSetOffset == 0 || ligatureSetOffset + 2 > subtable.Length) return false;

        var setBase = subtable[ligatureSetOffset..];
        var sr = new BigEndianReader(setBase);
        var ligatureCount = sr.ReadUInt16();

        Span<int> components = stackalloc int[MaxComponents];

        for (var l = 0; l < ligatureCount; l++)
        {
            var ligOffsetPos = 2 + l * 2;
            if (ligOffsetPos + 2 > setBase.Length) break;
            var ligatureOffset = ReadU16(setBase, ligOffsetPos);
            if (ligatureOffset == 0 || ligatureOffset + 4 > setBase.Length) continue;

            var lr = new BigEndianReader(setBase[ligatureOffset..]);
            var ligatureGlyph = lr.ReadUInt16();
            var componentCount = lr.ReadUInt16();
            if (componentCount is 0 or > MaxComponents) continue;

            if (TryMatchComponents(lookup, font, buffer, i, setBase, ligatureOffset,
                    componentCount, components, out var matchedCount))
            {
                buffer.Ligate(components[..matchedCount], ligatureGlyph);
                i++; // one glyph now sits where the first component was
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchComponents(Lookup lookup, ShapingFont font, ShapeBuffer buffer,
        int start, ReadOnlySpan<byte> setBase, int ligatureOffset, int componentCount,
        Span<int> components, out int matchedCount)
    {
        matchedCount = 0;
        components[0] = start; // first component is the coverage (current) glyph

        // componentGlyphIDs lists components 2..N (the first is implied by coverage),
        // starting at ligatureOffset + 4 (after ligatureGlyph + componentCount).
        var pos = start;
        for (var c = 1; c < componentCount; c++)
        {
            var next = GlyphIterator.Next(buffer, font.Gdef, lookup.Flags, lookup.MarkFilteringSet, pos);
            if (next < 0) return false;

            var wantPos = ligatureOffset + 4 + (c - 1) * 2;
            if (wantPos + 2 > setBase.Length) return false;
            var wantGlyph = ReadU16(setBase, wantPos);

            if (buffer.GlyphsMutable[next] != wantGlyph) return false;
            components[c] = next;
            pos = next;
        }

        matchedCount = componentCount;
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
