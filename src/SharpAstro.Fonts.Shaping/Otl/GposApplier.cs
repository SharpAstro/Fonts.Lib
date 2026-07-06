using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Tables.Hmtx;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GPOS positioning appliers. H1 built type 1 (single) and type 2 (pair); H2 adds the
/// mark-attachment types — 4 (mark-to-base), 5 (mark-to-ligature), and 6 (mark-to-mark) —
/// plus <see cref="Finish"/>, the post-lookup pass that turns the marks' anchor-relative
/// offsets into on-line positions. Cursive (type 3) and contextual (7/8) arrive later.
///
/// <para>Mark positioning records a raw <c>base-anchor − mark-anchor</c> offset and an
/// attachment link (<see cref="ShapeBuffer.AttachMark"/>); <see cref="Finish"/> then
/// zeroes mark advances and propagates the offsets along the attachment chain (subtracting
/// the advances between a mark and its base), mirroring HarfBuzz's order: apply GPOS →
/// zero mark widths → propagate attachment offsets.</para>
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/gpos</para>
/// </summary>
internal static class GposApplier
{
    public static bool Apply(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
        => lookup.Type switch
        {
            1 => ApplySingle(subtable, buffer, ref i),
            2 => ApplyPair(lookup, subtable, font, buffer, ref i),
            4 => ApplyMarkToBase(subtable, font, buffer, ref i),
            5 => ApplyMarkToLigature(subtable, font, buffer, ref i),
            6 => ApplyMarkToMark(lookup, subtable, font, buffer, ref i),
            _ => false, // 3 (cursive) / 7 / 8 — later stages
        };

    private static bool ApplySingle(ReadOnlySpan<byte> subtable, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 6) return false;
        var r = new BigEndianReader(subtable);
        var format = r.ReadUInt16();
        var coverageOffset = r.ReadUInt16();
        var valueFormat = r.ReadUInt16();

        var cov = Coverage.Parse(subtable, coverageOffset);
        var covIdx = cov.GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (covIdx < 0) return false;

        ValueRecord value;
        if (format == 1)
        {
            value = ValueRecord.Read(ref r, valueFormat);
        }
        else if (format == 2)
        {
            var valueCount = r.ReadUInt16();
            if (covIdx >= valueCount) return false;
            r.Skip(covIdx * ValueRecord.Size(valueFormat));
            value = ValueRecord.Read(ref r, valueFormat);
        }
        else
        {
            return false;
        }

        buffer.AddPosition(i, value.XAdvance, value.XPlacement, value.YPlacement);
        i++;
        return true;
    }

    private static bool ApplyPair(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 8) return false;
        var format = ReadU16(subtable, 0);

        var second = GlyphIterator.Next(buffer, font.Gdef, lookup.Flags, lookup.MarkFilteringSet, i);
        if (second < 0) return false;

        var firstGlyph = buffer.GlyphsMutable[i];
        var secondGlyph = buffer.GlyphsMutable[second];

        ValueRecord v1, v2;
        ushort valueFormat2;

        if (format == 1)
        {
            if (!TryPairFormat1(subtable, firstGlyph, secondGlyph, out v1, out v2, out valueFormat2))
                return false;
        }
        else if (format == 2)
        {
            if (!TryPairFormat2(subtable, firstGlyph, secondGlyph, out v1, out v2, out valueFormat2))
                return false;
        }
        else
        {
            return false;
        }

        buffer.AddPosition(i, v1.XAdvance, v1.XPlacement, v1.YPlacement);
        if (valueFormat2 != 0)
        {
            buffer.AddPosition(second, v2.XAdvance, v2.XPlacement, v2.YPlacement);
            i = second + 1; // both glyphs adjusted — continue past the pair
        }
        else
        {
            i = second; // second glyph may open the next pair (kerning chains)
        }
        return true;
    }

    private static bool TryPairFormat1(ReadOnlySpan<byte> subtable, uint firstGlyph, uint secondGlyph,
        out ValueRecord v1, out ValueRecord v2, out ushort valueFormat2)
    {
        v1 = default;
        v2 = default;
        valueFormat2 = 0;

        var r = new BigEndianReader(subtable);
        r.Skip(2); // format
        var coverageOffset = r.ReadUInt16();
        var valueFormat1 = r.ReadUInt16();
        valueFormat2 = r.ReadUInt16();
        var pairSetCount = r.ReadUInt16();

        var cov = Coverage.Parse(subtable, coverageOffset);
        var covIdx = cov.GetCoverageIndex(firstGlyph);
        if (covIdx < 0 || covIdx >= pairSetCount) return false;

        var setOffsetPos = 10 + covIdx * 2;
        if (setOffsetPos + 2 > subtable.Length) return false;
        var pairSetOffset = ReadU16(subtable, setOffsetPos);
        if (pairSetOffset == 0 || pairSetOffset + 2 > subtable.Length) return false;

        var setBase = subtable[pairSetOffset..];
        var pairValueCount = ReadU16(setBase, 0);
        var v1Size = ValueRecord.Size(valueFormat1);
        var v2Size = ValueRecord.Size(valueFormat2);
        var recordSize = 2 + v1Size + v2Size;

        // PairValueRecords are sorted by secondGlyph → binary search.
        int lo = 0, hi = pairValueCount - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var recOffset = 2 + mid * recordSize;
            if (recOffset + 2 > setBase.Length) return false;
            var sg = ReadU16(setBase, recOffset);
            if (secondGlyph < sg) hi = mid - 1;
            else if (secondGlyph > sg) lo = mid + 1;
            else
            {
                var vr = new BigEndianReader(setBase[(recOffset + 2)..]);
                v1 = ValueRecord.Read(ref vr, valueFormat1);
                v2 = ValueRecord.Read(ref vr, valueFormat2);
                return true;
            }
        }
        return false;
    }

    private static bool TryPairFormat2(ReadOnlySpan<byte> subtable, uint firstGlyph, uint secondGlyph,
        out ValueRecord v1, out ValueRecord v2, out ushort valueFormat2)
    {
        v1 = default;
        v2 = default;
        valueFormat2 = 0;

        var r = new BigEndianReader(subtable);
        r.Skip(2); // format
        var coverageOffset = r.ReadUInt16();
        var valueFormat1 = r.ReadUInt16();
        valueFormat2 = r.ReadUInt16();
        var classDef1Offset = r.ReadUInt16();
        var classDef2Offset = r.ReadUInt16();
        var class1Count = r.ReadUInt16();
        var class2Count = r.ReadUInt16();

        // First glyph must be covered (spec: coverage lists all first glyphs of the subtable).
        var cov = Coverage.Parse(subtable, coverageOffset);
        if (cov.GetCoverageIndex(firstGlyph) < 0) return false;

        var classDef1 = ClassDef.Parse(subtable, classDef1Offset);
        var classDef2 = ClassDef.Parse(subtable, classDef2Offset);
        var c1 = classDef1.GetClass(firstGlyph);
        var c2 = classDef2.GetClass(secondGlyph);
        if (c1 >= class1Count || c2 >= class2Count) return false;

        var v1Size = ValueRecord.Size(valueFormat1);
        var v2Size = ValueRecord.Size(valueFormat2);
        var cellSize = v1Size + v2Size;
        // Class1Records start at the 16-byte header; cell (c1,c2) is a flat index.
        var cellOffset = 16 + (c1 * class2Count + c2) * cellSize;
        if (cellOffset + cellSize > subtable.Length) return false;

        var vr = new BigEndianReader(subtable[cellOffset..]);
        v1 = ValueRecord.Read(ref vr, valueFormat1);
        v2 = ValueRecord.Read(ref vr, valueFormat2);
        return true;
    }

    // ---- Mark attachment (types 4/5/6) -----------------------------------------------
    // All three share a 12-byte MarkXxxPosFormat1 header:
    //   format(2), markCoverage(2), <base|lig|mark2>Coverage(2),
    //   markClassCount(2), markArray(2), <base|lig|mark2>Array(2).

    /// <summary>Type 4 — attach the current mark to the nearest preceding base glyph.</summary>
    private static bool ApplyMarkToBase(ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 12) return false;
        var markCoverageOffset = ReadU16(subtable, 2);
        var baseCoverageOffset = ReadU16(subtable, 4);
        var markClassCount = ReadU16(subtable, 6);
        var markArrayOffset = ReadU16(subtable, 8);
        var baseArrayOffset = ReadU16(subtable, 10);

        var markIdx = Coverage.Parse(subtable, markCoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (markIdx < 0) return false;

        // HarfBuzz forces IgnoreMarks for the base search regardless of the lookup's flags.
        var basePos = GlyphIterator.Prev(buffer, font.Gdef, LookupFlags.IgnoreMarks, 0, i);
        if (basePos < 0) return false;

        var baseIdx = Coverage.Parse(subtable, baseCoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[basePos]);
        if (baseIdx < 0) return false;

        if (!TryReadMark(subtable, markArrayOffset, markIdx, markClassCount, out var markClass, out var mx, out var my))
            return false;
        if (!TryReadAnchorMatrix(subtable, baseArrayOffset, baseIdx, markClass, markClassCount, out var bx, out var by))
            return false;

        buffer.AttachMark(i, basePos, bx - mx, by - my);
        i++;
        return true;
    }

    /// <summary>
    /// Type 5 — attach the current mark to a preceding ligature. Which ligature component
    /// the mark belongs to needs per-glyph ligature-component tracking (arrives with the
    /// H4 shapers); until then we attach to the last component, HarfBuzz's fallback when a
    /// mark carries no component index.
    /// </summary>
    private static bool ApplyMarkToLigature(ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 12) return false;
        var markCoverageOffset = ReadU16(subtable, 2);
        var ligCoverageOffset = ReadU16(subtable, 4);
        var markClassCount = ReadU16(subtable, 6);
        var markArrayOffset = ReadU16(subtable, 8);
        var ligArrayOffset = ReadU16(subtable, 10);

        var markIdx = Coverage.Parse(subtable, markCoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (markIdx < 0) return false;

        var ligPos = GlyphIterator.Prev(buffer, font.Gdef, LookupFlags.IgnoreMarks, 0, i);
        if (ligPos < 0) return false;

        var ligIdx = Coverage.Parse(subtable, ligCoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[ligPos]);
        if (ligIdx < 0) return false;

        if (!TryReadMark(subtable, markArrayOffset, markIdx, markClassCount, out var markClass, out var mx, out var my))
            return false;
        if (!TryReadLigatureAnchor(subtable, ligArrayOffset, ligIdx, markClass, markClassCount, out var bx, out var by))
            return false;

        buffer.AttachMark(i, ligPos, bx - mx, by - my);
        i++;
        return true;
    }

    /// <summary>Type 6 — attach the current mark to the preceding mark (mark stacking).</summary>
    private static bool ApplyMarkToMark(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
    {
        if (subtable.Length < 12) return false;
        var mark1CoverageOffset = ReadU16(subtable, 2);
        var mark2CoverageOffset = ReadU16(subtable, 4);
        var markClassCount = ReadU16(subtable, 6);
        var mark1ArrayOffset = ReadU16(subtable, 8);
        var mark2ArrayOffset = ReadU16(subtable, 10);

        var mark1Idx = Coverage.Parse(subtable, mark1CoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[i]);
        if (mark1Idx < 0) return false;

        // The base mark is the immediately preceding glyph WITH marks visible (HarfBuzz
        // clears only IgnoreMarks); it must itself be a mark to stack onto.
        var prevFlags = lookup.Flags & ~LookupFlags.IgnoreMarks;
        var prev = GlyphIterator.Prev(buffer, font.Gdef, prevFlags, lookup.MarkFilteringSet, i);
        if (prev < 0 || (GlyphClass)buffer.ClassesMutable[prev] != GlyphClass.Mark) return false;

        var mark2Idx = Coverage.Parse(subtable, mark2CoverageOffset).GetCoverageIndex(buffer.GlyphsMutable[prev]);
        if (mark2Idx < 0) return false;

        if (!TryReadMark(subtable, mark1ArrayOffset, mark1Idx, markClassCount, out var markClass, out var mx, out var my))
            return false;
        // Mark2Array is an anchor matrix [mark2Count][markClassCount], same shape as BaseArray.
        if (!TryReadAnchorMatrix(subtable, mark2ArrayOffset, mark2Idx, markClass, markClassCount, out var bx, out var by))
            return false;

        buffer.AttachMark(i, prev, bx - mx, by - my);
        i++;
        return true;
    }

    /// <summary>Read a MarkArray record: the mark's class and anchor. Fails on a bad index,
    /// an out-of-range class, or a NULL/invalid anchor (all "no attachment").</summary>
    private static bool TryReadMark(ReadOnlySpan<byte> subtable, int markArrayOffset, int markIndex,
        ushort markClassCount, out int markClass, out short x, out short y)
    {
        markClass = 0;
        x = 0;
        y = 0;
        if (markArrayOffset <= 0 || markArrayOffset + 2 > subtable.Length) return false;
        var markArray = subtable[markArrayOffset..];
        var markCount = ReadU16(markArray, 0);
        if ((uint)markIndex >= markCount) return false;

        var recOffset = 2 + markIndex * 4; // MarkRecord = markClass(2) + markAnchorOffset(2)
        if (recOffset + 4 > markArray.Length) return false;
        markClass = ReadU16(markArray, recOffset);
        if (markClass >= markClassCount) return false;
        var anchorOffset = ReadU16(markArray, recOffset + 2);
        return Anchor.TryGet(markArray, anchorOffset, out x, out y);
    }

    /// <summary>
    /// Read an anchor from a BaseArray/Mark2Array — a matrix of <c>[count][markClassCount]</c>
    /// Offset16 anchors — at (<paramref name="rowIndex"/>, <paramref name="markClass"/>).
    /// </summary>
    private static bool TryReadAnchorMatrix(ReadOnlySpan<byte> subtable, int arrayOffset, int rowIndex,
        int markClass, ushort markClassCount, out short x, out short y)
    {
        x = 0;
        y = 0;
        if (arrayOffset <= 0 || arrayOffset + 2 > subtable.Length) return false;
        var array = subtable[arrayOffset..];
        var rowCount = ReadU16(array, 0);
        if ((uint)rowIndex >= rowCount) return false;

        var anchorOffPos = 2 + (rowIndex * markClassCount + markClass) * 2;
        if (anchorOffPos + 2 > array.Length) return false;
        var anchorOffset = ReadU16(array, anchorOffPos);
        return Anchor.TryGet(array, anchorOffset, out x, out y);
    }

    /// <summary>Read a ligature anchor (type 5): LigatureArray → LigatureAttach[ligIndex] →
    /// componentRecords[last][markClass]. Uses the last component (see <see cref="ApplyMarkToLigature"/>).</summary>
    private static bool TryReadLigatureAnchor(ReadOnlySpan<byte> subtable, int ligArrayOffset, int ligIndex,
        int markClass, ushort markClassCount, out short x, out short y)
    {
        x = 0;
        y = 0;
        if (ligArrayOffset <= 0 || ligArrayOffset + 2 > subtable.Length) return false;
        var ligArray = subtable[ligArrayOffset..];
        var ligCount = ReadU16(ligArray, 0);
        if ((uint)ligIndex >= ligCount) return false;

        var attachOffPos = 2 + ligIndex * 2;
        if (attachOffPos + 2 > ligArray.Length) return false;
        var attachOffset = ReadU16(ligArray, attachOffPos);
        if (attachOffset == 0 || attachOffset + 2 > ligArray.Length) return false;

        var ligAttach = ligArray[attachOffset..];
        var componentCount = ReadU16(ligAttach, 0);
        if (componentCount == 0) return false;
        var comp = componentCount - 1; // last component (no lig-comp tracking until H4)

        var anchorOffPos = 2 + (comp * markClassCount + markClass) * 2;
        if (anchorOffPos + 2 > ligAttach.Length) return false;
        var anchorOffset = ReadU16(ligAttach, anchorOffPos);
        return Anchor.TryGet(ligAttach, anchorOffset, out x, out y);
    }

    /// <summary>
    /// Post-lookup positioning pass (HarfBuzz's zero-mark-widths + propagate-attachment-offsets):
    /// zero the advance of every mark (by GDEF class), then resolve each attachment chain so a
    /// mark's stored anchor-relative offset becomes its on-line offset — the parent's resolved
    /// offset plus the (negated) advances of the glyphs between the parent and the mark. Runs on
    /// the buffer in logical order, before any RTL reversal (RTL mark propagation lands with H4).
    /// </summary>
    public static void Finish(ShapingFont font, ShapeBuffer buffer)
    {
        var hmtx = font.Font.Hmtx;
        var glyphs = buffer.GlyphsMutable;
        var classes = buffer.ClassesMutable;
        var advDeltas = buffer.AdvDeltasMutable;
        var xOffsets = buffer.XOffsetsMutable;
        var yOffsets = buffer.YOffsetsMutable;
        var chains = buffer.AttachChainMutable;

        // 1) Zero mark advances first (so the propagation below never counts a mark's own
        // advance). Advances are stored as deltas → "zero" means delta = −(hmtx advance).
        if (hmtx is not null)
        {
            for (var k = 0; k < glyphs.Length; k++)
                if ((GlyphClass)classes[k] == GlyphClass.Mark)
                    advDeltas[k] = -hmtx.GetAdvanceWidth(glyphs[k]);
        }

        // 2) Resolve attachment chains (parent always precedes the mark in logical order).
        for (var k = 0; k < chains.Length; k++)
            ResolveChain(k, hmtx, glyphs, advDeltas, xOffsets, yOffsets, chains);
    }

    private static void ResolveChain(int i, HmtxTable? hmtx, ReadOnlySpan<uint> glyphs,
        ReadOnlySpan<int> advDeltas, Span<int> xOffsets, Span<int> yOffsets, Span<int> chains)
    {
        var chain = chains[i];
        if (chain == 0) return;
        chains[i] = 0; // clear before recursing — guards against a malformed cycle
        var j = i + chain; // parent; j < i by construction (base/prev-mark precede the mark)
        if ((uint)j >= (uint)glyphs.Length) return;

        ResolveChain(j, hmtx, glyphs, advDeltas, xOffsets, yOffsets, chains); // parent first

        xOffsets[i] += xOffsets[j];
        yOffsets[i] += yOffsets[j];

        // Subtract the absolute advances between parent and mark (horizontal: y-advance is 0).
        if (hmtx is not null)
            for (var k = j; k < i; k++)
                xOffsets[i] -= hmtx.GetAdvanceWidth(glyphs[k]) + advDeltas[k];
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
