using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GSUB substitution appliers implemented for H1: type 1 (single) and type 4
/// (ligature). Each tries to apply one subtable at buffer position <c>i</c>; on
/// success it mutates the buffer and advances <c>i</c> past the output, returning
/// true. Types 2/3 (multiple/alternate) and 5/6/8 (contextual/reverse) arrive in
/// later stages and currently no-op.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/gsub</para>
/// </summary>
internal static class GsubApplier
{
    // Ligatures with more components than this are ignored (real ligatures are 2–4;
    // the cap bounds the stack buffer for matched component indices).
    private const int MaxComponents = 16;

    public static bool Apply(Lookup lookup, ReadOnlySpan<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i)
        => lookup.Type switch
        {
            1 => ApplySingle(subtable, buffer, ref i),
            4 => ApplyLigature(lookup, subtable, font, buffer, ref i),
            _ => false, // 2/3/5/6/8 — later stages
        };

    private static bool ApplySingle(ReadOnlySpan<byte> subtable, ShapeBuffer buffer, ref int i)
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
            buffer.Substitute(i, (uint)((gid + delta) & 0xFFFF), GlyphClass.Base);
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
            buffer.Substitute(i, r.ReadUInt16(), GlyphClass.Base);
            i++;
            return true;
        }

        return false;
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
