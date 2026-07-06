using System.Buffers.Binary;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// GSUB type 2 (multiple) and type 3 (alternate) substitution. Unlike ligatures and
/// kerning, these aren't exercised by the DejaVu conformance fixtures (its default
/// feature set doesn't use them), so they're proven here against hand-assembled
/// subtables driven straight through <see cref="GsubApplier"/> — the same approach the
/// core's <c>GposExtensionKernTests</c> uses for a lookup shape no test font provides.
/// A real font backs the <see cref="ShapingFont"/> only so GDEF class re-derivation has
/// something to read; the glyph ids under test are synthetic.
/// </summary>
public class GsubMultipleAlternateTests
{
    private static ShapingFont Font()
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf")));

    private static ShapeBuffer BufferOf(params uint[] glyphIds)
    {
        var buffer = new ShapeBuffer();
        buffer.AddText(new string('.', glyphIds.Length)); // size the buffer; one BMP slot per glyph
        for (var i = 0; i < glyphIds.Length; i++)
        {
            buffer.GlyphsMutable[i] = glyphIds[i];
            buffer.ClassesMutable[i] = (byte)GlyphClass.Base;
        }
        return buffer;
    }

    private static Lookup LookupOf(ushort type) => new()
    {
        Type = type,
        Flags = LookupFlags.None,
        MarkFilteringSet = 0,
        Subtables = [], // Apply() takes the subtable span directly; this array is unused here
    };

    [Fact]
    public void MultipleSubst_ExpandsOneGlyphToSequence_SharingTheCluster()
    {
        // 100 → [200, 201, 202]
        var subtable = BuildMultiple(inputGid: 100, [200, 201, 202]);
        var buffer = BufferOf(100, 105); // trailing glyph proves the tail shifts right
        var i = 0;

        GsubApplier.Apply(LookupOf(2), subtable, Font(), buffer, ref i).ShouldBeTrue();

        buffer.Length.ShouldBe(4);
        buffer.GlyphIds.ToArray().ShouldBe([200u, 201u, 202u, 105u]);
        // All expansion glyphs inherit the input glyph's cluster (0); the tail keeps its own (1).
        buffer.Clusters.ToArray().ShouldBe([0, 0, 0, 1]);
        i.ShouldBe(3); // advanced past the whole expansion
    }

    [Fact]
    public void MultipleSubst_EmptySequence_DeletesTheGlyph()
    {
        // 100 → [] (a font spelling "remove this glyph")
        var subtable = BuildMultiple(inputGid: 100, []);
        var buffer = BufferOf(100, 105);
        var i = 0;

        GsubApplier.Apply(LookupOf(2), subtable, Font(), buffer, ref i).ShouldBeTrue();

        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(105u);
        i.ShouldBe(0); // stays on the glyph that shifted into place
    }

    [Fact]
    public void MultipleSubst_UncoveredGlyph_DoesNotApply()
    {
        var subtable = BuildMultiple(inputGid: 100, [200, 201]);
        var buffer = BufferOf(999); // not in coverage
        var i = 0;

        GsubApplier.Apply(LookupOf(2), subtable, Font(), buffer, ref i).ShouldBeFalse();
        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(999u);
        i.ShouldBe(0);
    }

    [Fact]
    public void AlternateSubst_PicksFirstAlternate()
    {
        // 100 → one of {300, 301}; the on/off feature model takes the first.
        var subtable = BuildAlternate(inputGid: 100, [300, 301]);
        var buffer = BufferOf(100);
        var i = 0;

        GsubApplier.Apply(LookupOf(3), subtable, Font(), buffer, ref i).ShouldBeTrue();

        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(300u);
        i.ShouldBe(1);
    }

    [Fact]
    public void AlternateSubst_UncoveredGlyph_DoesNotApply()
    {
        var subtable = BuildAlternate(inputGid: 100, [300, 301]);
        var buffer = BufferOf(50);
        var i = 0;

        GsubApplier.Apply(LookupOf(3), subtable, Font(), buffer, ref i).ShouldBeFalse();
        buffer.GlyphIds[0].ShouldBe(50u);
    }

    // ---- minimal subtable builders (offsets relative to the subtable start) ----

    /// <summary>MultipleSubstFormat1: header(8) + Coverage-fmt1 + one Sequence.</summary>
    private static byte[] BuildMultiple(ushort inputGid, ushort[] outputGlyphs)
    {
        var coverageOffset = 8;
        var sequenceOffset = coverageOffset + 6; // coverage fmt1 = format+count+1 glyph = 6 bytes
        var total = sequenceOffset + 2 + outputGlyphs.Length * 2;
        var b = new byte[total];
        WriteU16(b, 0, 1);                       // substFormat
        WriteU16(b, 2, (ushort)coverageOffset);  // coverageOffset
        WriteU16(b, 4, 1);                       // sequenceCount
        WriteU16(b, 6, (ushort)sequenceOffset);  // sequenceOffsets[0]
        WriteCoverage1(b, coverageOffset, inputGid);
        WriteU16(b, sequenceOffset, (ushort)outputGlyphs.Length);
        for (var g = 0; g < outputGlyphs.Length; g++)
            WriteU16(b, sequenceOffset + 2 + g * 2, outputGlyphs[g]);
        return b;
    }

    /// <summary>AlternateSubstFormat1: header(8) + Coverage-fmt1 + one AlternateSet.</summary>
    private static byte[] BuildAlternate(ushort inputGid, ushort[] alternates)
    {
        var coverageOffset = 8;
        var setOffset = coverageOffset + 6;
        var total = setOffset + 2 + alternates.Length * 2;
        var b = new byte[total];
        WriteU16(b, 0, 1);                      // substFormat
        WriteU16(b, 2, (ushort)coverageOffset); // coverageOffset
        WriteU16(b, 4, 1);                      // alternateSetCount
        WriteU16(b, 6, (ushort)setOffset);      // alternateSetOffsets[0]
        WriteCoverage1(b, coverageOffset, inputGid);
        WriteU16(b, setOffset, (ushort)alternates.Length);
        for (var g = 0; g < alternates.Length; g++)
            WriteU16(b, setOffset + 2 + g * 2, alternates[g]);
        return b;
    }

    private static void WriteCoverage1(byte[] b, int offset, ushort glyph)
    {
        WriteU16(b, offset, 1);     // coverageFormat
        WriteU16(b, offset + 2, 1); // glyphCount
        WriteU16(b, offset + 4, glyph);
    }

    private static void WriteU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
}
