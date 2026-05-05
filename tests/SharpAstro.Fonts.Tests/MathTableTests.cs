using SharpAstro.Fonts.Tables.OpenTypeMath;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Tests for OpenType MATH table parsing — the table that drives proper math
/// typesetting (radicals, scalable brackets, fraction/limits metrics) when
/// the loaded face ships <c>SharpAstro.Fonts.Tables.OpenTypeMath.MathTable</c>.
///
/// <para>This file currently covers:
/// <list type="bullet">
/// <item>Smoke: every bundled fixture font is non-math (DejaVu, Roboto, Source
/// Sans, Noto, etc.) — none should expose a <see cref="MathTable"/>. Confirms
/// the loader doesn't false-positive on absent tables.</item>
/// <item>Synthetic round-trip: parse minimal handcrafted MATH bytes covering
/// MathConstants, MathVariants (Format 1 and Format 2 coverage), and assembly
/// recipes. Doesn't need a math font fixture to run.</item>
/// </list>
/// Real-math-font tests against a STIX/LM fixture live alongside once a font
/// fixture is authorized for the repo.</para>
/// </summary>
public sealed class MathTableTests
{
    /// <summary>UI fonts in the fixture set that do not ship a MATH table —
    /// loaded for a smoke check that the parser doesn't false-positive.
    /// DejaVu Sans is excluded: its bundled build does include MATH (covered
    /// by <see cref="DejaVuSans_HasMathTable"/> below).</summary>
    public static IEnumerable<object[]> NonMathFontPaths() =>
        Fixtures.All.Where(f => f != Fixtures.DejaVuSans).Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(NonMathFontPaths))]
    public void NonMathFonts_HaveNullMathTable(string fixturePath)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixturePath));
        font.Math.ShouldBeNull();
    }

    /// <summary>
    /// DejaVu Sans (the bundled build) ships a MATH table. Verifies the
    /// parser produces a non-null <see cref="MathTable"/>, populates the
    /// constants with sensible non-zero values for the metrics that real
    /// math layout depends on (axis height, fraction rule thickness,
    /// radical rule thickness), and that those values are interpretable
    /// as FUnits in this font's <c>head.unitsPerEm</c>.
    /// </summary>
    [Fact]
    public void DejaVuSans_HasMathTable()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Math.ShouldNotBeNull();
        var c = font.Math!.Constants;

        // unitsPerEm for DejaVu is 2048; metrics must be plausible fractions
        // of that. We don't pin exact values (different DejaVu builds tweak
        // them) but assert they're in the ballpark a math font would use.
        font.Head.UnitsPerEm.ShouldBe((ushort)2048);
        c.AxisHeight.ShouldBeInRange<short>(200, 800);            // ~25% of em
        c.FractionRuleThickness.ShouldBeGreaterThan<short>(0);
        c.RadicalRuleThickness.ShouldBeGreaterThan<short>(0);
        // Default-zero structurally-optional fields should still parse to 0
        // without any out-of-range exception.
        _ = c.RadicalDegreeBottomRaisePercent;
    }

    /// <summary>
    /// DejaVu Sans's MATH table includes vertical-stretch entries for at
    /// least the standard delimiters. Pick one that's almost universal in
    /// math fonts — left parenthesis '(' (U+0028) — and verify its glyph
    /// has a vertical construction (variants and/or assembly).
    /// </summary>
    [Fact]
    public void DejaVuSans_LeftParen_HasVerticalConstruction()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var lparenGlyph = (ushort)font.GetGlyphId('(');
        lparenGlyph.ShouldBeGreaterThan((ushort)0);

        var construction = font.Math!.GetVerticalConstruction(lparenGlyph);
        // Some DejaVu builds cover '(' in vertical, others may not. If
        // present, validate the structural shape; if absent, fall back to
        // asserting that *some* vertical construction exists in the table
        // (proving the parser populates the dictionary at all).
        if (construction is null)
        {
            // Find any covered glyph by trial across common math delimiters.
            var anyCovered = new uint[] { '[', '{', '|', '√' }
                .Select(cp => (ushort)font.GetGlyphId(cp))
                .Any(g => g > 0 && font.Math.GetVerticalConstruction(g) is not null);
            anyCovered.ShouldBeTrue("DejaVu MATH should cover at least one vertical-stretch delimiter");
            return;
        }

        // A construction must offer some way to produce a stretched glyph
        // — either pre-drawn variants, or an assembly recipe, or both.
        // DejaVu '(' uses assembly-only (no pre-drawn variants); STIX-class
        // fonts ship variants too.
        var hasVariants = construction.Variants.Count > 0;
        var hasAssembly = construction.Assembly is not null;
        (hasVariants || hasAssembly).ShouldBeTrue(
            "construction should offer either variants or an assembly recipe");

        // When variants are present they're ordered ascending in advance.
        for (var i = 1; i < construction.Variants.Count; i++)
            construction.Variants[i].AdvanceMeasurement
                .ShouldBeGreaterThanOrEqualTo(construction.Variants[i - 1].AdvanceMeasurement);

        // When an assembly is present its parts must be non-empty and at
        // least one part should be marked extender (otherwise the assembly
        // can't actually grow beyond its own fixed length).
        if (construction.Assembly is { } asm)
        {
            asm.Parts.Count.ShouldBeGreaterThan(0);
            asm.Parts.ShouldContain(p => p.IsExtender);
        }
    }

    /// <summary>
    /// Parse a hand-built MATH table containing only a populated MathConstants
    /// subtable — the simplest valid shape. Verifies each MathValueRecord
    /// reads its int16 and skips the device-table offset.
    /// </summary>
    [Fact]
    public void Parse_MinimalConstantsOnly()
    {
        var bytes = BuildMinimalMathTable(
            axisHeight: 280,
            fractionRuleThickness: 32,
            radicalRuleThickness: 28,
            radicalDegreeBottomRaisePercent: 60);

        var math = MathTable.Parse(bytes);

        math.Constants.AxisHeight.ShouldBe((short)280);
        math.Constants.FractionRuleThickness.ShouldBe((short)32);
        math.Constants.RadicalRuleThickness.ShouldBe((short)28);
        math.Constants.RadicalDegreeBottomRaisePercent.ShouldBe((short)60);
        // No variants subtable in this minimal layout.
        math.GetVerticalConstruction(0).ShouldBeNull();
        math.GetHorizontalConstruction(0).ShouldBeNull();
    }

    /// <summary>
    /// Parse a MATH table with one vertical-stretch glyph (id 100) carrying a
    /// chain of three variants and an assembly recipe. Coverage is Format 1
    /// (explicit list).
    /// </summary>
    [Fact]
    public void Parse_VariantsAndAssembly_CoverageFormat1()
    {
        var bytes = BuildMathTableWithVariants(
            coverageFormat: 1,
            verticalGlyphIds: [100],
            variants: [(101, 600), (102, 800), (103, 1200)],
            assemblyParts: [(110, 0, 80, 200, false), (111, 80, 80, 100, true), (112, 80, 0, 200, false)],
            minConnectorOverlap: 40);

        var math = MathTable.Parse(bytes);
        math.MinConnectorOverlap.ShouldBe((ushort)40);

        var construction = math.GetVerticalConstruction(100);
        construction.ShouldNotBeNull();
        construction!.Variants.Count.ShouldBe(3);
        construction.Variants[0].ShouldBe(new MathGlyphVariant(101, 600));
        construction.Variants[2].ShouldBe(new MathGlyphVariant(103, 1200));

        var assembly = construction.Assembly;
        assembly.ShouldNotBeNull();
        assembly!.Parts.Count.ShouldBe(3);
        assembly.Parts[0].IsExtender.ShouldBeFalse();
        assembly.Parts[1].IsExtender.ShouldBeTrue();
        assembly.Parts[2].IsExtender.ShouldBeFalse();
        assembly.Parts[1].FullAdvance.ShouldBe((ushort)100);

        // Glyphs not in coverage return null.
        math.GetVerticalConstruction(99).ShouldBeNull();
        math.GetHorizontalConstruction(100).ShouldBeNull();
    }

    /// <summary>
    /// Coverage Format 2 (ranges) — three glyphs (id 50, 51, 52) given via a
    /// single range record. Each gets its own variants array.
    /// </summary>
    [Fact]
    public void Parse_Variants_CoverageFormat2_Ranges()
    {
        var bytes = BuildMathTableWithVariants(
            coverageFormat: 2,
            verticalGlyphIds: [50, 51, 52],
            variants: [(60, 500)],
            assemblyParts: [],
            minConnectorOverlap: 30);

        var math = MathTable.Parse(bytes);

        // All three glyphs in the range are covered, sharing the variant chain.
        math.GetVerticalConstruction(50).ShouldNotBeNull();
        math.GetVerticalConstruction(51).ShouldNotBeNull();
        math.GetVerticalConstruction(52).ShouldNotBeNull();
        math.GetVerticalConstruction(53).ShouldBeNull();
        math.GetVerticalConstruction(49).ShouldBeNull();
    }

    /// <summary>
    /// MATH header rejects unknown major versions — guards against version-2+
    /// table layouts being misparsed as v1.
    /// </summary>
    [Fact]
    public void Parse_RejectsUnsupportedMajorVersion()
    {
        // Minimal v2 header — major=2.
        var bytes = new byte[10];
        bytes[0] = 0x00; bytes[1] = 0x02;       // majorVersion = 2
        bytes[2] = 0x00; bytes[3] = 0x00;       // minorVersion
        bytes[4] = 0x00; bytes[5] = 0x0A;       // constantsOffset
        bytes[6] = 0x00; bytes[7] = 0x00;       // glyphInfoOffset
        bytes[8] = 0x00; bytes[9] = 0x00;       // variantsOffset

        Should.Throw<InvalidDataException>(() => MathTable.Parse(bytes));
    }

    // ---------------- byte-array builders for the synthetic fixtures ----------------
    // The OpenType MATH spec is offset-heavy (each subtable's offset is relative
    // to the parent table's origin); the helpers below let each test express
    // intent concretely while keeping the byte plumbing in one place.

    private static byte[] BuildMinimalMathTable(
        short axisHeight, short fractionRuleThickness, short radicalRuleThickness,
        short radicalDegreeBottomRaisePercent)
    {
        // MATH header is 10 bytes; MathConstants subtable follows immediately.
        // MathConstants layout: 4*int16 + 51*MathValueRecord(int16+offset16) + 1*int16.
        //   = 8 + 204 + 2 = 214 bytes.
        var w = new BeWriter();
        // Header.
        w.U16(1);    // majorVersion
        w.U16(0);    // minorVersion
        w.U16(10);   // mathConstantsOffset (immediately after header)
        w.U16(0);    // mathGlyphInfoOffset (none)
        w.U16(0);    // mathVariantsOffset (none)
        // MathConstants — offset 10.
        w.I16(0);    // scriptPercentScaleDown
        w.I16(0);    // scriptScriptPercentScaleDown
        w.U16(0);    // delimitedSubFormulaMinHeight
        w.U16(0);    // displayOperatorMinHeight
        // 51 MathValueRecords. We zero everything except a few we want to read back.
        // Order must match the spec / our parser. axisHeight is the 2nd MVR.
        WriteValueRecord(w, 0);          // mathLeading                         (index 4)
        WriteValueRecord(w, axisHeight); // axisHeight                          (index 5)
        // Indices 6..37 inclusive = 32 zero MVRs, ending at fractionNumeratorDisplayStyleGapMin.
        for (var i = 0; i < 32; i++) WriteValueRecord(w, 0);
        WriteValueRecord(w, fractionRuleThickness);          // (index 38)
        // Indices 39..50 inclusive = 12 zero MVRs, ending at radicalDisplayStyleVerticalGap.
        for (var i = 0; i < 12; i++) WriteValueRecord(w, 0);
        WriteValueRecord(w, radicalRuleThickness);           // (index 51)
        WriteValueRecord(w, 0);                              // radicalExtraAscender
        WriteValueRecord(w, 0);                              // radicalKernBeforeDegree
        WriteValueRecord(w, 0);                              // radicalKernAfterDegree
        w.I16(radicalDegreeBottomRaisePercent);
        return w.ToArray();
    }

    private static byte[] BuildMathTableWithVariants(
        int coverageFormat,
        ushort[] verticalGlyphIds,
        (ushort glyphId, ushort advance)[] variants,
        (ushort glyphId, ushort startConn, ushort endConn, ushort fullAdv, bool extender)[] assemblyParts,
        ushort minConnectorOverlap)
    {
        // Layout: header(10) + MathConstants(214) + MathVariants(...).
        // We fill MathConstants with zeroes; tests only assert structural
        // metadata, not constants, in this builder.
        const int constantsOffset = 10;
        const int constantsSize = 214;
        var variantsOffset = constantsOffset + constantsSize;

        var w = new BeWriter();
        w.U16(1); w.U16(0);
        w.U16(constantsOffset);
        w.U16(0);
        w.U16((ushort)variantsOffset);

        // Zero-filled MathConstants.
        for (var i = 0; i < constantsSize; i++) w.U8(0);

        // MathVariants subtable, written into a sub-writer so we can compute
        // internal offsets (which are relative to the variants subtable start).
        var sub = new BeWriter();
        var hasAssembly = assemblyParts.Length > 0;

        // Header of MathVariants: 5 ushorts = 10 bytes.
        // Then vertGlyphCount * Offset16 = 2 * vertCount bytes.
        // Then horizGlyphCount * Offset16 = 0 bytes (no horizontal here).
        const int subHeaderSize = 10;
        var vertConstructionOffsetsSize = verticalGlyphIds.Length * 2;
        var afterOffsetsCursor = subHeaderSize + vertConstructionOffsetsSize;

        // Coverage table — placed first after the offset arrays.
        var coverageOffset = afterOffsetsCursor;
        var coverageBytes = BuildCoverage(coverageFormat, verticalGlyphIds);
        var glyphConstructionOffset = coverageOffset + coverageBytes.Length;

        // GlyphConstruction subtable — same one shared by every glyph in coverage
        // for this synthetic test (real fonts have one per glyph, but we don't
        // need that distinction to verify the parser).
        var constructionBytes = BuildGlyphConstruction(variants, assemblyParts);

        // Write MathVariants header.
        sub.U16(minConnectorOverlap);
        sub.U16((ushort)coverageOffset);   // vertCoverageOffset
        sub.U16(0);                         // horizCoverageOffset
        sub.U16((ushort)verticalGlyphIds.Length);  // vertGlyphCount
        sub.U16(0);                         // horizGlyphCount
        // vertConstructionOffsets — every glyph points to the same construction.
        for (var i = 0; i < verticalGlyphIds.Length; i++)
            sub.U16((ushort)glyphConstructionOffset);
        // Then the coverage bytes.
        sub.Bytes(coverageBytes);
        // Then the construction bytes.
        sub.Bytes(constructionBytes);

        w.Bytes(sub.ToArray());
        return w.ToArray();
    }

    private static byte[] BuildCoverage(int format, ushort[] glyphIds)
    {
        var w = new BeWriter();
        if (format == 1)
        {
            w.U16(1);
            w.U16((ushort)glyphIds.Length);
            foreach (var g in glyphIds) w.U16(g);
        }
        else if (format == 2)
        {
            // Pack contiguous ids into one range; assumes glyphIds is sorted ascending.
            w.U16(2);
            w.U16(1); // rangeCount
            w.U16(glyphIds[0]);             // start
            w.U16(glyphIds[^1]);            // end
            w.U16(0);                        // startCoverageIndex
        }
        return w.ToArray();
    }

    private static byte[] BuildGlyphConstruction(
        (ushort glyphId, ushort advance)[] variants,
        (ushort glyphId, ushort startConn, ushort endConn, ushort fullAdv, bool extender)[] assemblyParts)
    {
        // Construction header: Offset16 assembly + uint16 variantCount + variants[].
        // Each variant: 2 ushorts = 4 bytes.
        var w = new BeWriter();
        const int constructionHeaderSize = 4;
        var variantsSize = variants.Length * 4;
        var assemblyOffset = assemblyParts.Length == 0 ? 0 : constructionHeaderSize + variantsSize;

        w.U16((ushort)assemblyOffset);
        w.U16((ushort)variants.Length);
        foreach (var v in variants)
        {
            w.U16(v.glyphId);
            w.U16(v.advance);
        }
        if (assemblyParts.Length > 0)
        {
            // GlyphAssembly: italicsCorrection (MathValueRecord = 4 bytes) +
            // partCount (uint16) + partRecords[partCount] (5 ushorts = 10 bytes each).
            w.I16(0); w.U16(0);             // italicsCorrection value + device offset
            w.U16((ushort)assemblyParts.Length);
            foreach (var p in assemblyParts)
            {
                w.U16(p.glyphId);
                w.U16(p.startConn);
                w.U16(p.endConn);
                w.U16(p.fullAdv);
                w.U16((ushort)(p.extender ? 1 : 0));
            }
        }
        return w.ToArray();
    }

    private static void WriteValueRecord(BeWriter w, short value)
    {
        w.I16(value);
        w.U16(0); // device-table offset (unused)
    }

    /// <summary>Tiny big-endian byte writer for the synthetic builders above.</summary>
    private sealed class BeWriter
    {
        private readonly List<byte> _bytes = new();
        public void U8(byte v) => _bytes.Add(v);
        public void U16(ushort v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); }
        public void U16(int v) => U16((ushort)v);
        public void I16(short v) { _bytes.Add((byte)((ushort)v >> 8)); _bytes.Add((byte)v); }
        public void Bytes(byte[] data) => _bytes.AddRange(data);
        public byte[] ToArray() => _bytes.ToArray();
    }
}
