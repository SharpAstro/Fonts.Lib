using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GPOS positioning appliers implemented for H1: type 1 (single adjustment) and
/// type 2 (pair adjustment, both formats). Values are added to the buffer's delta
/// arrays (font units). Marks (4/5/6), cursive (3), and contextual (7/8) arrive in
/// later stages and currently no-op.
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
            _ => false, // 3/4/5/6/7/8 — later stages
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

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
