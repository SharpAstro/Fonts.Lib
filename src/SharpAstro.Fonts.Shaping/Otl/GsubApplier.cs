using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GSUB substitution appliers. H1 built type 1 (single) and type 4 (ligature); H2 adds
/// type 2 (multiple) and type 3 (alternate). Each tries to apply one subtable at buffer
/// position <c>i</c>; on success it mutates the buffer and advances <c>i</c> past the
/// output, returning true. Substituted glyphs re-derive their GDEF class from the font so
/// downstream mark processing sees the right classes (e.g. a <c>ccmp</c> that maps a
/// codepoint to a mark glyph). Contextual/reverse (types 5/6/8) arrive in later stages.
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

    public static bool Apply(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
        => lookup.Type switch
        {
            1 => ApplySingle(subtable, font, buffer, ref i),
            2 => ApplyMultiple(subtable, font, buffer, ref i),
            3 => ApplyAlternate(subtable, font, buffer, ref i),
            4 => ApplyLigature(lookup, subtable, font, buffer, ref i),
            _ => false, // 5/6/8 — later stages
        };

    private static bool ApplySingle(ReadOnlySpan<byte> subtable, ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        var coverageOffset = r.ReadUInt16();
        var cov = Coverage.Parse(subtable, coverageOffset);

        var gid = buffer.GlyphsMutable[i];
        var covIdx = cov.GetCoverageIndex(gid);
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

        var cov = Coverage.Parse(subtable, coverageOffset);
        var covIdx = cov.GetCoverageIndex(buffer.GlyphsMutable[i]);
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

        var cov = Coverage.Parse(subtable, coverageOffset);
        var covIdx = cov.GetCoverageIndex(buffer.GlyphsMutable[i]);
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

        var cov = Coverage.Parse(subtable, coverageOffset);
        var covIdx = cov.GetCoverageIndex(buffer.GlyphsMutable[i]);
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
