using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Tables.Cff;
using SharpAstro.Fonts.Tables.Cmap;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The Type 2 charstring's two-byte operators (Adobe TN #5177): the four flex operators (§4.1) and the
/// arithmetic, logic and transient-array operators (§4.3-4.4). The interpreter used to skip all of them,
/// clearing the stack. A flex is two curves that move the pen, so skipped it left the rest of the glyph drawn
/// displaced by the flex's travel: a CFF subset converted from Type 1 (NewBaskerville in a Manning book) drew
/// its capitals and half its lowercase as smears. Each charstring here is written by hand from the spec's
/// operand order, and the expected points are the spec's arithmetic, not this reader's output.
/// </summary>
public class Type2ExtendedOperatorTests
{
    [Fact]
    public void Flex_DrawsBothCurves_AndMovesThePen()
    {
        // 0 0 rmoveto; flex: dx1..dy6 = (10,5) (10,5) (10,0) (10,0) (10,-5) (10,-5), fd 50; 7 0 rlineto
        var path = Run(Ops(0, 0), Op(21),
            Ops(10, 5, 10, 5, 10, 0, 10, 0, 10, -5, 10, -5, 50), Esc(35),
            Ops(7, 0), Op(5), Op(14));

        path.ShouldBe([
            "M 0 0",
            "C 10 5 20 10 30 10",
            "C 40 10 50 5 60 0",
            "L 67 0",
            "Z",
        ]);
    }

    [Fact]
    public void HFlex_EndsOnTheStartingY()
    {
        // hflex: dx1 dx2 dy2 dx3 dx4 dx5 dx6 = 10 10 6 10 10 10 10
        var path = Run(Ops(0, 0), Op(21), Ops(10, 10, 6, 10, 10, 10, 10), Esc(34), Ops(0, 9), Op(5), Op(14));

        path.ShouldBe([
            "M 0 0",
            "C 10 0 20 6 30 6",
            "C 40 6 50 0 60 0",
            "L 60 9",
            "Z",
        ]);
    }

    [Fact]
    public void HFlex1_ReturnsToTheStartingY()
    {
        // hflex1: dx1 dy1 dx2 dy2 dx3 dx4 dx5 dy5 dx6 = 10 2 10 3 10 10 10 -1 10; dy6 = -(2 + 3 - 1) = -4
        var path = Run(Ops(0, 0), Op(21), Ops(10, 2, 10, 3, 10, 10, 10, -1, 10), Esc(36), Ops(0, 9), Op(5), Op(14));

        path.ShouldBe([
            "M 0 0",
            "C 10 2 20 5 30 5",
            "C 40 5 50 4 60 0",
            "L 60 9",
            "Z",
        ]);
    }

    [Fact]
    public void Flex1_HorizontalTravel_D6IsX_AndYReturns()
    {
        // Points 1-5 travel 50 in x and 4 in y, so d6 (8) is dx6 and dy6 = -4.
        var path = Run(Ops(0, 0), Op(21), Ops(10, 1, 10, 1, 10, 0, 10, 1, 10, 1, 8), Esc(37), Ops(0, 9), Op(5), Op(14));

        path.ShouldBe([
            "M 0 0",
            "C 10 1 20 2 30 2",
            "C 40 3 50 4 58 0",
            "L 58 9",
            "Z",
        ]);
    }

    [Fact]
    public void Flex1_VerticalTravel_D6IsY_AndXReturns()
    {
        // Points 1-5 travel 50 in y and -4 in x, so d6 (8) is dy6 and dx6 = 4.
        var path = Run(Ops(0, 0), Op(21), Ops(-1, 10, -1, 10, 0, 10, -1, 10, -1, 10, 8), Esc(37), Ops(9, 0), Op(5), Op(14));

        path.ShouldBe([
            "M 0 0",
            "C -1 10 -2 20 -2 30",
            "C -3 40 -4 50 0 58",
            "L 9 58",
            "Z",
        ]);
    }

    [Fact]
    public void Arithmetic_LeavesItsResultForTheNextOperator()
    {
        // (10 + 20) (7 * 3) rmoveto; (9 - 4) (100 / 4) rlineto; (-(-3)) |-6| rlineto
        var path = Run(Ops(10, 20), Esc(10), Ops(7, 3), Esc(24), Op(21),
            Ops(9, 4), Esc(11), Ops(100, 4), Esc(12), Op(5),
            Ops(-3), Esc(14), Ops(-6), Esc(9), Op(5), Op(14));

        path.ShouldBe(["M 30 21", "L 35 46", "L 38 52", "Z"]);
    }

    [Fact]
    public void StackOperators_DupExchIndexRoll()
    {
        // 4 dup rmoveto -> (4,4); 1 2 exch rlineto -> (+2,+1); 5 6 0 index -> 5 6 6, drop -> 5 6, rlineto;
        // 1 2 3 3 1 roll -> 3 1 2, drop -> 3 1, rlineto
        var path = Run(Ops(4), Esc(27), Op(21),
            Ops(1, 2), Esc(28), Op(5),
            Ops(5, 6, 0), Esc(29), Esc(18), Op(5),
            Ops(1, 2, 3, 3, 1), Esc(30), Esc(18), Op(5), Op(14));

        path.ShouldBe(["M 4 4", "L 6 5", "L 11 11", "L 14 12", "Z"]);
    }

    [Fact]
    public void TransientArray_AndIfElse()
    {
        // 25 0 put; 0 get 0 rmoveto -> (25,0); 1 2 3 4 ifelse (3 <= 4 -> 1), 2 2 1 0 ifelse (1 > 0 -> 2): rlineto (1,2)
        var path = Run(Ops(25, 0), Esc(20), Ops(0), Esc(21), Ops(0), Op(21),
            Ops(1, 2, 3, 4), Esc(22), Ops(2, 2, 1, 0), Esc(22), Op(5), Op(14));

        path.ShouldBe(["M 25 0", "L 26 2", "Z"]);
    }

    // ---- accented composites (endchar with four operands, TN #5177 Appendix C) -----------------------------

    // A font converted from Type 1 builds its accented letters as "adx ady bchar achar endchar": the base and
    // the accent by StandardEncoding code, the accent moved by (adx, ady). With the operands dropped, a German
    // subset's ä, ö and ü drew nothing at all. The font here is a bare CFF written byte by byte: a 100-unit
    // square 'u', a 20-unit square 'dieresis', and 'udieresis' composed of the two.
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // with the optional width ahead of the four operands
    public void Composite_DrawsBase_ThenAccentAtItsOffset(bool withWidth)
    {
        var font = OpenTypeFont.Load(CompositeCff(withWidth));
        var gid = font.GetGlyphIdByName("udieresis");
        gid.ShouldNotBe(0u);

        var sink = new RecordingSink();
        font.DrawGlyph(gid, sink);

        sink.Path.ShouldBe([
            "M 0 0", "L 100 0", "L 100 100", "L 0 100", "Z",
            "M 40 150", "L 60 150", "L 60 170", "L 40 170", "Z",
        ]);
    }

    [Fact]
    public void StandardEncoding_NamesTheAccents()
    {
        // The Type 1 interpreter's own table held the letters alone, so every accent a seac named was .notdef.
        AdobeStandardEncoding.NameOf(117).ShouldBe("u");
        AdobeStandardEncoding.NameOf(193).ShouldBe("grave");
        AdobeStandardEncoding.NameOf(194).ShouldBe("acute");
        AdobeStandardEncoding.NameOf(200).ShouldBe("dieresis");
        AdobeStandardEncoding.NameOf(251).ShouldBe("germandbls");
        AdobeStandardEncoding.NameOf(128).ShouldBe(".notdef");
        AdobeStandardEncoding.NameOf(400).ShouldBe(".notdef");
    }

    // ---- the built-in encoding (Top DICT op 16) ------------------------------------------------------------

    // A PDF simple font with no /Encoding selects through the font's own encoding (ISO 32000 §9.6.5). A TeX math
    // subset (MathDesign, CMSY) carries a custom one whose codes are not glyph indices: taken as indices, a German
    // homework sheet drew 'bardbl' for every bullet (code 15, glyph 15) and nothing for '(' or '='.
    // The glyphs here are .notdef, u (1), dieresis (2), udieresis (3).

    [Fact]
    public void BuiltInEncoding_Format0_MapsCodesNotIndices()
    {
        // Format 0: one code per glyph from GID 1: u = 15, dieresis = 40, udieresis = 61.
        var font = OpenTypeFont.Load(CompositeCff(false, encoding: [0, 3, 15, 40, 61]));

        font.GetGlyphId(0, 15, GlyphMapHint.EmbeddedSubset).ShouldBe(1u);
        font.GetGlyphId(0, 40, GlyphMapHint.EmbeddedSubset).ShouldBe(2u);
        font.GetGlyphId(0, 61, GlyphMapHint.EmbeddedSubset).ShouldBe(3u);
    }

    [Fact]
    public void BuiltInEncoding_Format1_WithSupplement()
    {
        // Format 1 with the supplement bit: one range of codes 70-72 for GIDs 1-3, then a supplement giving
        // code 200 to the glyph named 'dieresis' as well.
        var dieresis = CffStandardStrings.IndexOf("dieresis");
        var font = OpenTypeFont.Load(CompositeCff(false,
            encoding: [0x81, 1, 70, 2, 1, 200, (byte)(dieresis >> 8), (byte)dieresis]));

        font.GetGlyphId(0, 70, GlyphMapHint.EmbeddedSubset).ShouldBe(1u);
        font.GetGlyphId(0, 71, GlyphMapHint.EmbeddedSubset).ShouldBe(2u);
        font.GetGlyphId(0, 72, GlyphMapHint.EmbeddedSubset).ShouldBe(3u);
        font.GetGlyphId(0, 200, GlyphMapHint.EmbeddedSubset).ShouldBe(2u);
    }

    [Fact]
    public void BuiltInEncoding_DefaultsToStandard_ThenTheCodeAsIndex()
    {
        // No Encoding operator: StandardEncoding, so 'u' is code 117 and 'dieresis' 200. A code it does not map
        // still falls back to being the glyph index, as before.
        var font = OpenTypeFont.Load(CompositeCff(false));

        font.GetGlyphId(0, 117, GlyphMapHint.EmbeddedSubset).ShouldBe(1u);
        font.GetGlyphId(0, 200, GlyphMapHint.EmbeddedSubset).ShouldBe(2u);
        font.GetGlyphId(0, 3, GlyphMapHint.EmbeddedSubset).ShouldBe(3u);
    }

    private static byte[] CompositeCff(bool withWidth, byte[]? encoding = null)
    {
        byte[] Square(int side) => [.. Ops(0, 0), 21, .. Ops(side), 6, .. Ops(side), 7, .. Ops(-side), 6, 14];
        byte[][] charStrings =
        [
            [14],                                                                       // .notdef
            Square(100),                                                                // u
            Square(20),                                                                 // dieresis
            [.. withWidth ? Ops(500) : [], .. Ops(40, 150, 117, 200), 14],             // udieresis
        ];
        ushort[] sids =
        [
            (ushort)CffStandardStrings.IndexOf("u"),
            (ushort)CffStandardStrings.IndexOf("dieresis"),
            (ushort)CffStandardStrings.IndexOf("udieresis"),
        ];

        // Header, Name INDEX, Top DICT INDEX (fixed-size DICT: every offset a 5-byte integer), String INDEX and
        // Global Subr INDEX (both empty), then the charset, CharStrings, the encoding if any, and an empty Private.
        var topDictSize = 23 + (encoding is null ? 0 : 6);
        var head = 4 + Index([[(byte)'T']]).Length + (2 + 1 + 2 + topDictSize) + 2 + 2;
        var charsetOffset = head;
        var charset = new List<byte> { 0 };
        foreach (var sid in sids) { charset.Add((byte)(sid >> 8)); charset.Add((byte)sid); }
        var charStringsOffset = charsetOffset + charset.Count;
        var charStringsIndex = Index(charStrings);
        var encodingOffset = charStringsOffset + charStringsIndex.Length;
        var privateOffset = encodingOffset + (encoding?.Length ?? 0);

        var topDict = new List<byte>();
        if (encoding is not null) { topDict.AddRange(Int5(encodingOffset)); topDict.Add(16); }
        topDict.AddRange(Int5(charsetOffset)); topDict.Add(15);
        topDict.AddRange(Int5(charStringsOffset)); topDict.Add(17);
        topDict.AddRange(Int5(0)); topDict.AddRange(Int5(privateOffset)); topDict.Add(18);
        topDict.Count.ShouldBe(topDictSize);

        var cff = new List<byte> { 1, 0, 4, 4 };
        cff.AddRange(Index([[(byte)'T']]));
        cff.AddRange(Index([topDict.ToArray()]));
        cff.AddRange(Index([]));
        cff.AddRange(Index([]));
        cff.Count.ShouldBe(charsetOffset);
        cff.AddRange(charset);
        cff.AddRange(charStringsIndex);
        if (encoding is not null) cff.AddRange(encoding);
        return cff.ToArray();
    }

    // An INDEX with one-byte offsets (every object here is small).
    private static byte[] Index(byte[][] objects)
    {
        if (objects.Length == 0) return [0, 0];
        var bytes = new List<byte> { (byte)(objects.Length >> 8), (byte)objects.Length, 1 };
        var offset = 1;
        bytes.Add((byte)offset);
        foreach (var o in objects) { offset += o.Length; bytes.Add((byte)offset); }
        foreach (var o in objects) bytes.AddRange(o);
        return bytes.ToArray();
    }

    private static byte[] Int5(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    // ---- charstring assembly ---------------------------------------------------------------------------

    private static List<string> Run(params byte[][] parts)
    {
        var sink = new RecordingSink();
        Type2CharstringInterpreter.Execute(parts.SelectMany(p => p).ToArray(), CffIndex.Empty, CffIndex.Empty, sink);
        return sink.Path;
    }

    private static byte[] Op(byte op) => [op];

    private static byte[] Esc(byte op) => [12, op];

    // Integer operands in -1131..1131 (TN #5177 §3.2).
    private static byte[] Ops(params int[] values)
    {
        var bytes = new List<byte>();
        foreach (var v in values)
        {
            if (v is >= -107 and <= 107) bytes.Add((byte)(v + 139));
            else if (v is >= 108 and <= 1131)
            {
                var w = v - 108;
                bytes.Add((byte)((w >> 8) + 247));
                bytes.Add((byte)(w & 0xFF));
            }
            else if (v is >= -1131 and <= -108)
            {
                var w = -v - 108;
                bytes.Add((byte)((w >> 8) + 251));
                bytes.Add((byte)(w & 0xFF));
            }
            else throw new ArgumentOutOfRangeException(nameof(values), v, "outside the short integer forms");
        }
        return bytes.ToArray();
    }

    private sealed class RecordingSink : IGlyphSink
    {
        public List<string> Path { get; } = [];

        private static string N(float v) => MathF.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public void MoveTo(float x, float y) => Path.Add($"M {N(x)} {N(y)}");
        public void LineTo(float x, float y) => Path.Add($"L {N(x)} {N(y)}");
        public void QuadTo(float cx, float cy, float x, float y) => Path.Add($"Q {N(cx)} {N(cy)} {N(x)} {N(y)}");
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => Path.Add($"C {N(c1x)} {N(c1y)} {N(c2x)} {N(c2y)} {N(x)} {N(y)}");
        public void Close() => Path.Add("Z");
    }
}
