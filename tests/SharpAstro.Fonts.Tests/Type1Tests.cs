using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Type1;

namespace SharpAstro.Fonts.Tests;

public class Type1Tests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");

    static Type1Tests() => Directory.CreateDirectory(DumpDir);

    private const string CmrFile = "cmr10.pfb";

    [Fact]
    public void Cmr10_LoadsAndHasGlyphNames()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        font.GlyphNames.Count.ShouldBeGreaterThan(0);
        // Computer Modern Roman uses TeX-style encoding; common letters are present.
        font.HasGlyph("A").ShouldBeTrue();
        font.HasGlyph("B").ShouldBeTrue();
        font.HasGlyph(".notdef").ShouldBeTrue();
    }

    [Fact]
    public void Cmr10_FontMatrix_IsTypicalType1()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        // Standard Type 1: [0.001 0 0 0.001 0 0]
        font.FontMatrix[0].ShouldBe(0.001f, tolerance: 0.0001f);
        font.FontMatrix[3].ShouldBe(0.001f, tolerance: 0.0001f);
        font.UnitsPerEm.ShouldBe(1000);
    }

    [Fact]
    public void Cmr10_DrawGlyph_EmitsCommands()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var sink = new CountingSink();
        var ok = font.DrawGlyph("A", sink);
        ok.ShouldBeTrue();
        sink.MoveCount.ShouldBeGreaterThan(0);
        // Type 1 uses cubic Béziers; well-formed letters should have at least one curve.
        (sink.CubicCount + sink.LineCount).ShouldBeGreaterThan(0);
        sink.CloseCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("g")]
    [InlineData("Q")]
    public void Cmr10_RenderGlyph_ProducesAaBitmap(string name)
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var bmp = font.RenderGlyph(name, 64f);
        bmp.IsEmpty.ShouldBeFalse();
        bmp.Alpha.Max().ShouldBe((byte)255);
        bmp.Alpha.Min().ShouldBe((byte)0);
        bmp.Alpha.Any(a => a > 0 && a < 255).ShouldBeTrue();
    }

    [Fact]
    public void Cmr10_DumpAaBmps()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        foreach (var name in new[] { "A", "B", "g", "Q", "comma", "period" })
        {
            if (!font.HasGlyph(name)) continue;
            var bmp = font.RenderGlyph(name, 64f);
            if (bmp.IsEmpty) continue;
            BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir, $"cmr10_{name}_64px.bmp"),
                bmp.Alpha, bmp.Width, bmp.Height);
        }
    }

    [Fact]
    public void Cmr10_IsConcurrentlyRenderable()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var expected = font.RenderGlyph("A", 32f);
        Parallel.For(0, 64, _ =>
        {
            var bmp = font.RenderGlyph("A", 32f);
            bmp.Width.ShouldBe(expected.Width);
            bmp.Height.ShouldBe(expected.Height);
        });
    }

    [Fact]
    public void Closepath_LeavesCurrentPointUnchanged()
    {
        // Type 1 closepath must NOT reposition the current point (unlike PostScript closepath). A
        // following rmoveto is therefore relative to the contour's LAST point, not its start — so a
        // second contour (an i/j dot, an accent) lands correctly. Regression: the interpreter used
        // to reset to the start, floating every multi-contour glyph's later contours.
        var cs = new Cs()
            .N(0).N(1000).Op(13)     // hsbw
            .N(100).N(700).Op(21)    // rmoveto -> contour 1 start (100,700)
            .N(60).N(0).Op(5)        // rlineto -> (160,700)
            .N(0).N(-40).Op(5)       // rlineto -> (160,660)
            .N(-60).N(0).Op(5)       // rlineto -> (100,660)  (last point)
            .Op(9)                   // closepath
            .N(0).N(-300).Op(21)     // rmoveto -> contour 2 start
            .Op(14)                  // endchar
            .Done();
        var sink = new RecordingSink();
        Type1CharstringInterpreter.Execute(cs, [], sink);

        sink.Moves.Count.ShouldBe(2);
        // Relative to the LAST point (100,660): correct = 360. The old bug reset to the contour
        // start (100,700) and produced 400.
        sink.Moves[1].Y.ShouldBe(360f, tolerance: 0.5f);
    }

    [Fact]
    public void Flex_CollapsesSevenMovetosIntoTwoCurves()
    {
        // OtherSubrs 0/1/2 implement flex: the 7 rmovetos after OtherSubr 1 are control points, not
        // new contours, and OtherSubr 0 collapses them into two cubic curves. Regression: callothersubr
        // was a no-op, so the 7 moves drew as strokes and curves never formed.
        var cs = new Cs().N(0).N(1000).Op(13).N(100).N(100).Op(21) // hsbw + rmoveto to (100,100)
            .N(0).N(1).Esc(16);                                    // 0 1 callothersubr (flex start)
        for (var k = 0; k < 7; k++)
            cs.N(10).N(10).Op(21).N(0).N(2).Esc(16);               // 7x (rmoveto + 0 2 callothersubr)
        cs.N(50).N(170).N(170).N(3).N(0).Esc(16)                   // flex end: height endx endy 3 0 callothersubr
          .Esc(17).Esc(17).Esc(33)                                 // pop pop setcurrentpoint
          .Op(14);                                                 // endchar
        var sink = new RecordingSink();
        Type1CharstringInterpreter.Execute(cs.Done(), [], sink);

        sink.Moves.Count.ShouldBe(1);          // only the initial rmoveto; the 7 flex moves suppressed
        sink.Cubics.Count.ShouldBe(2);         // the two flex curves
        sink.Cubics[^1].EndY.ShouldBe(170f, tolerance: 0.5f);
    }

    [Fact]
    public void LoadType1_RawProgram_MatchesPfb()
    {
        // A PDF /FontFile carries raw Type 1 (clear text + eexec), not a .pfb wrapper. Reconstruct the
        // raw form from the fixture and confirm LoadType1 parses it equivalently to LoadPfb.
        var pfb = File.ReadAllBytes(Fixtures.Path(CmrFile));
        var (ascii, binary) = PfbReader.Read(pfb);
        // PfbReader concatenates the clear header (segment 1, ending in "eexec") with the trailing
        // zeros (segment 3); cut at the eexec token so the raw form is header + sep + eexec binary.
        var headerLen = ascii.AsSpan().IndexOf("eexec"u8) + 5;
        var raw = new byte[headerLen + 1 + binary.Length];
        Array.Copy(ascii, raw, headerLen);
        raw[headerLen] = (byte)'\n';
        Array.Copy(binary, 0, raw, headerLen + 1, binary.Length);

        var fromPfb = Type1Font.LoadPfb(pfb);
        var fromRaw = Type1Font.LoadType1(raw);
        fromRaw.GlyphNames.Count.ShouldBe(fromPfb.GlyphNames.Count);
        fromRaw.HasGlyph("A").ShouldBeTrue();
        fromRaw.RenderGlyph("A", 32f).Width.ShouldBe(fromPfb.RenderGlyph("A", 32f).Width);
    }

    private sealed class CountingSink : IGlyphSink
    {
        public int MoveCount, LineCount, QuadCount, CubicCount, CloseCount;
        public void MoveTo(float x, float y) => MoveCount++;
        public void LineTo(float x, float y) => LineCount++;
        public void QuadTo(float cx, float cy, float x, float y) => QuadCount++;
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => CubicCount++;
        public void Close() => CloseCount++;
    }

    private sealed class RecordingSink : IGlyphSink
    {
        public readonly List<(float X, float Y)> Moves = new();
        public readonly List<(float EndX, float EndY)> Cubics = new();
        public void MoveTo(float x, float y) => Moves.Add((x, y));
        public void LineTo(float x, float y) { }
        public void QuadTo(float cx, float cy, float x, float y) { }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Cubics.Add((x, y));
        public void Close() { }
    }

    // Minimal Type 1 charstring byte builder — operand encoding per the Type 1 spec §6.4.
    private sealed class Cs
    {
        private readonly List<byte> _b = [];
        public Cs N(int v)
        {
            switch (v)
            {
                case >= -107 and <= 107: _b.Add((byte)(v + 139)); break;
                case >= 108 and <= 1131: v -= 108; _b.Add((byte)(247 + (v >> 8))); _b.Add((byte)(v & 0xff)); break;
                case >= -1131 and <= -108: v = -108 - v; _b.Add((byte)(251 + (v >> 8))); _b.Add((byte)(v & 0xff)); break;
                default: _b.Add(255); _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 8)); _b.Add((byte)v); break;
            }
            return this;
        }
        public Cs Op(int op) { _b.Add((byte)op); return this; }
        public Cs Esc(int ext) { _b.Add(12); _b.Add((byte)ext); return this; }
        public byte[] Done() => [.. _b];
    }
}
