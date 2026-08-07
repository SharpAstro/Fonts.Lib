using System.Globalization;
using System.Text;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Shx;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// AutoCAD SHX shape-font tests, against the two fixtures authored from scratch by
/// <c>tools/make_shx_fixtures.py</c> (Autodesk's stock faces are their IP and cannot be
/// bundled). Because the fixtures are synthetic their geometry is known exactly, so most
/// of these assert the full emitted path rather than command counts.
/// </summary>
public class ShxTests
{
    private const string UnifontName = "SHARPASTRO TEST UNIFONT";
    private const string BigfontName = "SHARPASTRO TEST BIGFONT";

    private static ShxFont Unifont() => ShxFont.LoadFromFile(Fixtures.Path(Fixtures.ShxTestUnifont));
    private static ShxFont Bigfont() => ShxFont.LoadFromFile(Fixtures.Path(Fixtures.ShxTestBigfont));

    // ---------------------------------------------------------------- phase 1: container

    [Fact]
    public void Unifont_Loads_WithFontDefinitionMetrics()
    {
        var font = Unifont();
        font.Format.ShouldBe(ShxFormat.Unifont);
        font.Header.ShouldBe("AutoCAD-86 unifont 1.0");
        font.Name.ShouldBe(UnifontName);
        font.Above.ShouldBe(8);
        font.Below.ShouldBe(2);
        // There is no unitsPerEm field in SHX; the em is above + below.
        font.UnitsPerEm.ShouldBe(10);
        font.Modes.ShouldBe(0);
        font.HasVerticalForms.ShouldBeFalse();
        font.LeadByteRanges.ShouldBeEmpty();
    }

    [Fact]
    public void Unifont_HasTheFixtureGlyphs_KeyedByCodePoint()
    {
        var font = Unifont();
        font.Codes.ShouldBe([0x2D, 0x41, 0x49, 0x4C, 0x4F, 0x54, 0x5A]);
        foreach (var c in "ILAOZT-") font.HasGlyph(c).ShouldBeTrue();
        font.HasGlyph('Q').ShouldBeFalse();
    }

    [Fact]
    public void Shapes_Header_IsRejected_WithAnErrorNamingTheReason()
    {
        // simplex.shx and ACAD.SHX are symbol libraries addressed by shape number, not text
        // fonts. Read with the unifont layout they yield a few nonsense glyphs and then run
        // off the end of a record, so they must be refused by header. In a 4,428-file survey
        // they were the majority of .shx files, so this is the common path.
        var shapes = Encoding.ASCII.GetBytes("AutoCAD-86 shapes 1.0\r\n\x1a").Concat(
            new byte[] { 0x02, 0x00, 0x01, 0x00, 0x10, 0x00 }).ToArray();

        var ex = Should.Throw<NotSupportedException>(() => ShxFont.Load(shapes));
        ex.Message.ShouldContain("shape number");
    }

    [Theory]
    [InlineData("AutoCAD-86 potato 1.0\r\n\x1a")]   // valid terminator, unknown layout
    [InlineData("not an shx file at all\x1a")]
    public void UnrecognisedHeader_IsRejected(string header)
        => Should.Throw<InvalidDataException>(() => ShxFont.Load(Encoding.ASCII.GetBytes(header)));

    [Fact]
    public void MissingHeaderTerminator_IsRejected()
        => Should.Throw<InvalidDataException>(
            () => ShxFont.Load(Encoding.ASCII.GetBytes(new string('x', 64))));

    [Fact]
    public void TruncatedFile_IsRejected_RatherThanIndexingPastTheEnd()
    {
        var full = File.ReadAllBytes(Fixtures.Path(Fixtures.ShxTestUnifont));
        // Header plus two bytes: not enough for the glyph count and definition length.
        Should.Throw<InvalidDataException>(() => ShxFont.Load(full.AsSpan(0, 27)));
    }

    [Fact]
    public void TruncatedGlyphRecords_KeepWhatParsed_RatherThanThrowing()
    {
        // Real faces contain truncated records; a usable prefix beats an exception. Cut the
        // fixture mid-way through its records and check the earlier ones survive.
        var full = File.ReadAllBytes(Fixtures.Path(Fixtures.ShxTestUnifont));
        var font = ShxFont.Load(full.AsSpan(0, 100));
        font.Name.ShouldBe(UnifontName);
        font.Codes.Length.ShouldBeGreaterThan(0);
        font.Codes.Length.ShouldBeLessThan(7);
    }

    [Fact]
    public void TrailingBytesAfterTheRecords_AreIgnored()
    {
        // 16 of 170 surveyed unifont faces carry a 48-byte ASCII GUID watermark appended by
        // some authoring tool. It is not a record and must not be read as one.
        var full = File.ReadAllBytes(Fixtures.Path(Fixtures.ShxTestUnifont));
        var watermarked = full.Concat(
            Encoding.ASCII.GetBytes("1924a0f3-4a1a-4c48-89a2-748e334c55dc08.05.02.27")).ToArray();

        var font = ShxFont.Load(watermarked);
        font.Codes.ShouldBe(Unifont().Codes);
        Trace(font, 'I').ShouldBe(Trace(Unifont(), 'I'));
    }

    // ------------------------------------------------- phase 2: the opcode interpreter

    [Theory]
    // A bare vertical bar: zero width, the case that breaks per-axis normalisation.
    [InlineData('I', "M(0,0) L(0,8)")]
    // A bare horizontal bar: zero height, the mirror case.
    [InlineData('-', "M(0,2) L(6,2)")]
    // Pen lift plus push/pop (0x05/0x06) to return to the origin for the second stroke.
    [InlineData('L', "M(0,0) L(0,8) M(0,0) L(5,0)")]
    // Signed XY displacements (0x08) rather than packed vectors.
    [InlineData('A', "M(0,0) L(3,8) L(6,0) M(1,3) L(5,3)")]
    // A displacement run (0x09), plus a 0x0E vertical-only command that must be skipped.
    [InlineData('Z', "M(0,0) L(6,0) L(0,-8) L(6,-8)")]
    // A crossbar plus 'I' pulled in by subshape reference (0x07).
    [InlineData('T', "M(-3,8) L(3,8) M(0,0) L(0,8)")]
    public void Glyph_EmitsExactlyTheExpectedPath(char ch, string expected)
        => Trace(Unifont(), ch).ShouldBe(expected);

    [Fact]
    public void UnifontSubshapeOperand_IsReadHighByteFirst()
    {
        // 0x07's 2-byte operand is big-endian, unlike every length and count in the
        // container. 'T' references 0x0049; read the other way that is 0x4900, which the
        // font does not define, and the crossbar would be all that survives. Measured
        // across 170 stock faces: 3,181 of 3,185 references resolve big-endian, 9 little.
        var sink = new PathSink();
        Unifont().TryGetGlyph('T', sink).ShouldBeTrue();
        sink.Ops.Count(o => o == 'M').ShouldBe(2);
        sink.LineCount.ShouldBe(2);
    }

    [Fact]
    public void SelfReferentialSubshape_TerminatesInsteadOfRecursingForever()
    {
        // 0x07 pointing at its own record. A depth guard is the only thing between this and
        // a stack overflow, and damaged faces are the norm rather than the exception here.
        var sink = new PathSink();
        Synth(0x01, 0x60, 0x07, 0x00, (byte)'X').TryGetGlyph('X', sink).ShouldBeTrue();
        sink.LineCount.ShouldBeGreaterThan(0);
        sink.LineCount.ShouldBeLessThan(32);
    }

    [Fact]
    public void NonEmptyGlyphName_IsConsumed_NotInterpretedAsOpcodes()
    {
        // 'Z' is named "zed". Every record begins with a NUL-terminated name, and an
        // interpreter that starts at byte 0 reads the terminator as end-of-shape and
        // returns an empty glyph for every character in the font — while the font still
        // loads cleanly and simply draws nothing. This is that regression.
        var sink = new PathSink();
        Unifont().TryGetGlyph('Z', sink).ShouldBeTrue();
        sink.Ops.Count.ShouldBe(4);
        sink.Ops[0].ShouldBe('M');
    }

    [Fact]
    public void PenPath_IsLeftOpen_NeverClosed()
    {
        // SHX produces a pen path with a width from the graphics state, not closed contours
        // to fill. Close() would claim a fillable contour the format does not have.
        var font = Unifont();
        foreach (var code in font.Codes)
        {
            var sink = new PathSink();
            font.TryGetGlyph(code, sink).ShouldBeTrue();
            sink.CloseCount.ShouldBe(0);
        }
    }

    [Fact]
    public void UnknownCode_ReturnsFalse_AndEmitsNothing()
    {
        var sink = new PathSink();
        Unifont().TryGetGlyph('Q', sink).ShouldBeFalse();
        sink.Ops.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ phase 3: arcs

    [Fact]
    public void O_IsAFullCircle_BuiltFromFourOctantArcs()
    {
        // The regression this fixture exists for: an interpreter that skips 0x0A returns an
        // empty glyph here. In the wild that shows up as a perfectly bimodal per-character
        // result -- 100% on T Y + 7 : and 0% on D O R P a c e g -- and txt.shx cannot reveal
        // it, because txt contains no arcs at all.
        var sink = new PathSink();
        Unifont().TryGetGlyph('O', sink).ShouldBeTrue();

        sink.CubicCount.ShouldBe(8);                       // four 90-degree arcs, 45 degrees per cubic
        sink.Ops.Count(o => o == 'M').ShouldBe(1);
        sink.LineCount.ShouldBe(0);

        // Every emitted point sits on the circle of radius 4 centred at (0,4). The centre is
        // never stated by the format: the pen lies ON the circle at the start angle, so it is
        // back along that radius.
        foreach (var (x, y) in sink.Points)
            MathF.Sqrt(x * x + (y - 4f) * (y - 4f)).ShouldBe(4f, tolerance: 0.02f);

        // And it closes back onto its start point.
        var first = sink.Points[0];
        var last = sink.Points[^1];
        last.X.ShouldBe(first.X, tolerance: 1e-3f);
        last.Y.ShouldBe(first.Y, tolerance: 1e-3f);
    }

    [Fact]
    public void RoundGlyph_IsNotEmpty()
    {
        // The blunt version of the check above, stated separately because it is the symptom
        // an arc-skipping decoder actually presents.
        var sink = new PathSink();
        Unifont().TryGetGlyph('O', sink);
        sink.Ops.ShouldNotBeEmpty();
    }

    [Fact]
    public void FractionalArc_WithZeroOffsets_MatchesTheEquivalentOctantArc()
    {
        // 0x0B reduces to 0x0A when both fractional offsets are zero. That constraint is
        // what pins down the reading of its operands, so it is worth asserting directly:
        // one quarter-circle of radius 4 from octant 0, expressed both ways.
        var octant = Synth(0x01, 0x0A, 4, 0x02);
        var fractional = Synth(0x01, 0x0B, 0, 0, 0, 4, 0x02);

        var a = new PathSink();
        var b = new PathSink();
        octant.TryGetGlyph('X', a).ShouldBeTrue();
        fractional.TryGetGlyph('X', b).ShouldBeTrue();

        a.CubicCount.ShouldBe(2);
        b.Points.Count.ShouldBe(a.Points.Count);
        for (var i = 0; i < a.Points.Count; i++)
        {
            b.Points[i].X.ShouldBe(a.Points[i].X, tolerance: 1e-3f);
            b.Points[i].Y.ShouldBe(a.Points[i].Y, tolerance: 1e-3f);
        }
    }

    [Fact]
    public void OctantArc_WithZeroCount_IsTheFullCircle()
    {
        // For 0x0A a count of 0 means eight octants. (For 0x0B it means zero -- an arc
        // contained inside a single octant -- which is the one place the two disagree.)
        var sink = new PathSink();
        Synth(0x01, 0x0A, 4, 0x00).TryGetGlyph('X', sink).ShouldBeTrue();
        sink.CubicCount.ShouldBe(8);
        sink.Points[^1].X.ShouldBe(0f, tolerance: 1e-3f);
        sink.Points[^1].Y.ShouldBe(0f, tolerance: 1e-3f);
    }

    [Fact]
    public void ClockwiseOctantArc_SweepsTheOtherWay()
    {
        var ccw = new PathSink();
        var cw = new PathSink();
        Synth(0x01, 0x0A, 4, 0x02).TryGetGlyph('X', ccw).ShouldBeTrue();
        Synth(0x01, 0x0A, 4, 0x82).TryGetGlyph('X', cw).ShouldBeTrue();

        // Starting at the pen and sweeping 90 degrees from octant 0: counterclockwise ends
        // up and left of the start, clockwise down and left.
        ccw.Points[^1].Y.ShouldBeGreaterThan(0f);
        cw.Points[^1].Y.ShouldBeLessThan(0f);
        ccw.Points[^1].X.ShouldBe(cw.Points[^1].X, tolerance: 1e-3f);
    }

    [Fact]
    public void BulgeArc_BowsBySagitta_AndZeroBulgeIsAStraightLine()
    {
        // 0x0C dx dy bulge, where bulge = 127 * 2H/D. |bulge| = 127 is therefore a
        // semicircle: over a chord of 8 along +X the arc should reach 4 off the chord.
        var semicircle = new PathSink();
        Synth(0x01, 0x0C, 8, 0, 127).TryGetGlyph('X', semicircle).ShouldBeTrue();
        semicircle.Points[^1].X.ShouldBe(8f, tolerance: 1e-3f);
        semicircle.Points[^1].Y.ShouldBe(0f, tolerance: 1e-3f);
        // Positive bulge is counterclockwise, which bows the arc to the right of the chord.
        semicircle.Points.Min(p => p.Y).ShouldBe(-4f, tolerance: 0.05f);

        var mirrored = new PathSink();
        Synth(0x01, 0x0C, 8, 0, unchecked((byte)-127)).TryGetGlyph('X', mirrored).ShouldBeTrue();
        mirrored.Points.Max(p => p.Y).ShouldBe(4f, tolerance: 0.05f);

        // Zero bulge is a plain line, and faces do use it.
        var flat = new PathSink();
        Synth(0x01, 0x0C, 8, 0, 0).TryGetGlyph('X', flat).ShouldBeTrue();
        flat.CubicCount.ShouldBe(0);
        flat.LineCount.ShouldBe(1);
        Format(flat).ShouldBe("M(0,0) L(8,0)");
    }

    [Fact]
    public void BulgeRun_DrawsEveryArc_AndStopsAtTheZeroPair()
    {
        // 0x0D is triples terminated by a (0,0) displacement, which carries no bulge byte.
        // Miscounting that terminator desynchronises everything after it.
        var sink = new PathSink();
        Synth(0x01, 0x0D, 4, 0, 60, 4, 0, 60, 0, 0, 0x60).TryGetGlyph('X', sink).ShouldBeTrue();

        // Both arcs land on their chord endpoints. How many cubics each takes is an
        // implementation detail -- an arc is split at 45 degrees and these sweep about 101 --
        // so the endpoints are what is asserted.
        sink.CubicCount.ShouldBeGreaterThan(0);
        sink.Points.ShouldContain(p => MathF.Abs(p.X - 4f) < 1e-3f && MathF.Abs(p.Y) < 1e-3f);
        sink.Points.ShouldContain(p => MathF.Abs(p.X - 8f) < 1e-3f && MathF.Abs(p.Y) < 1e-3f);

        // The 0x60 after the terminator is a packed vector (length 6, direction 0), so if the
        // run consumed one byte too many or too few this line would not land at x = 8 + 6.
        sink.Points[^1].X.ShouldBe(14f, tolerance: 1e-3f);
        sink.Ops[^1].ShouldBe('L');
    }

    // -------------------------------------------------------------- phase 4: bigfont

    [Fact]
    public void Bigfont_Loads_WithRangesAndDoubleByteCodes()
    {
        var font = Bigfont();
        font.Format.ShouldBe(ShxFormat.BigFont);
        font.Name.ShouldBe(BigfontName);
        font.Above.ShouldBe(8);
        font.Below.ShouldBe(2);
        font.Codes.ShouldBe([0x8141, 0x8142, 0x8143]);
        font.LeadByteRanges.ShouldBe([(0x81, 0x81)]);
    }

    [Fact]
    public void Bigfont_RecordsComeFromAnIndexTable_NotInlineLikeUnifont()
    {
        // The layouts genuinely differ: unifont stores (code, length, data) inline, bigfont
        // stores an 8-byte index entry (code, length, u32 offset) per record followed by a
        // contiguous data area. Reading bigfont with the unifont layout overruns EOF on 344
        // of 362 stock faces, so the two cannot share a path. Asserted behaviourally: every
        // indexed record resolves to real geometry.
        var font = Bigfont();
        font.Codes.Length.ShouldBe(3);
        foreach (var code in font.Codes)
        {
            var sink = new PathSink();
            font.TryGetGlyph(code, sink).ShouldBeTrue();
            sink.Ops.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void BigfontExtendedSubshape_ScalesTheRadicalIntoItsPlacementBox()
    {
        // 0x8143 composes the 6x6 box at 0x8142 through the 0x07/0x00 escape form, offset to
        // (2,1) and scaled into a 4-wide by 8-high box against above=8 — so X halves and Y is
        // unchanged, which is why the box comes out 3 wide and 6 tall.
        Trace(Bigfont(), 0x8143).ShouldBe("M(2,1) L(5,1) L(5,7) L(2,7) L(2,1)");

        // And the parent's pen is restored afterwards rather than left where the radical
        // finished: the trailing 9-unit move lands at 9, not at 9 offset by the box.
        Bigfont().TryGetAdvance(0x8143, out var advance).ShouldBeTrue();
        advance.ShouldBe(9f, tolerance: 1e-3f);
    }

    [Fact]
    public void Bigfont_LeadByteRanges_IdentifyTheEncodingFamilyOnly()
    {
        // The ranges say which bytes lead a double-byte sequence, never which codepage, so
        // codes stay opaque here and the caller supplies the mapping to Unicode.
        var font = Bigfont();
        font.IsLeadByte(0x81).ShouldBeTrue();
        font.IsLeadByte(0x80).ShouldBeFalse();
        font.IsLeadByte(0x82).ShouldBeFalse();
        Unifont().IsLeadByte(0x81).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0x8141, "M(0,-4) L(0,4) M(-4,0) L(4,0)")]   // a cross
    [InlineData(0x8142, "M(0,0) L(6,0) L(6,6) L(0,6) L(0,0)")]  // a box
    public void BigfontGlyph_EmitsExactlyTheExpectedPath(int code, string expected)
        => Trace(Bigfont(), code).ShouldBe(expected);

    // --------------------------------------------- phase 5: advances, vertical forms

    [Theory]
    [InlineData('I', 0f)]     // a bare vertical stroke never advances
    [InlineData('-', 6f)]
    [InlineData('L', 5f)]
    [InlineData('A', 7f)]
    [InlineData('O', 4f)]
    [InlineData('Z', 6f)]
    public void Advance_IsThePenPositionAtEndOfShape(char ch, float expected)
    {
        // The only advance SHX states. There is no hmtx, no kerning and no unitsPerEm.
        Unifont().TryGetAdvance(ch, out var advance).ShouldBeTrue();
        advance.ShouldBe(expected, tolerance: 1e-3f);
    }

    [Fact]
    public void Advance_ForUnknownCode_IsFalse()
        => Unifont().TryGetAdvance('Q', out _).ShouldBeFalse();

    [Fact]
    public void VerticalOrientation_RunsThe0x0ECommand_HorizontalSkipsIt()
    {
        // 0x0E is a skip, not a no-op: it suppresses the FOLLOWING command in horizontal
        // text. 'Z' carries one, so the two orientations must differ -- vertically it first
        // drops 4 units (the pen starts DOWN, so that move draws).
        var font = Unifont();
        Trace(font, 'Z', ShxTextOrientation.Horizontal)
            .ShouldBe("M(0,0) L(6,0) L(0,-8) L(6,-8)");
        Trace(font, 'Z', ShxTextOrientation.Vertical)
            .ShouldBe("M(0,0) L(0,-4) L(6,-4) L(0,-12) L(6,-12)");
    }

    [Fact]
    public void Orientation_DoesNotAffectGlyphsWithout0x0E()
    {
        var font = Unifont();
        foreach (var ch in "ILA-")
            Trace(font, ch, ShxTextOrientation.Vertical).ShouldBe(Trace(font, ch));
    }

    [Fact]
    public void PenStartsDown_SoAnUnguardedFirstStrokeStillDraws()
    {
        // Established from the corpus: 4,869 records draw before issuing any pen command,
        // and there are 50,202 more pen-ups than pen-downs across 44,332 records -- about
        // one unmatched lift each, the signature of a pen-down default.
        var sink = new PathSink();
        Synth(0x24).TryGetGlyph('X', sink).ShouldBeTrue();   // a packed vector, no 0x01 first
        Format(sink).ShouldBe("M(0,0) L(0,2)");
    }

    [Fact]
    public void PackedVectorDirections_AreNotUnitVectors()
    {
        // The minor axis is 0.5, not sin(22.5 degrees). That is what puts SHX diagonals on a
        // lattice and keeps them crisp at small sizes; "correcting" them skews every diagonal.
        var sink = new PathSink();
        Synth(0x01, 0x11).TryGetGlyph('X', sink).ShouldBeTrue();  // length 1, direction 1
        sink.Points[^1].X.ShouldBe(1f, tolerance: 1e-4f);
        sink.Points[^1].Y.ShouldBe(0.5f, tolerance: 1e-4f);
    }

    [Fact]
    public void ScaleCommands_DivideAndMultiplyVectorLength()
    {
        // 0x03 divides, 0x04 multiplies, and both persist until reversed.
        var sink = new PathSink();
        Synth(0x01, 0x04, 3, 0x10, 0x03, 6, 0x10).TryGetGlyph('X', sink).ShouldBeTrue();
        sink.Points[1].X.ShouldBe(3f, tolerance: 1e-4f);          // 1 * 3
        sink.Points[2].X.ShouldBe(3.5f, tolerance: 1e-4f);        // + 1 * 3/6
    }

    [Fact]
    public void PoppingAnEmptyStack_IsIgnored_NotThrown()
    {
        // The corpus holds 12,770 pops against 12,660 pushes, so some faces pop an empty
        // stack. Underflow and overflow are both survivable.
        var sink = new PathSink();
        Synth(0x01, 0x06, 0x06, 0x60).TryGetGlyph('X', sink).ShouldBeTrue();
        Format(sink).ShouldBe("M(0,0) L(6,0)");
    }

    // ------------------------------------------------------- phase 6: the stroker

    [Fact]
    public void Face_ReportsItselfStroked()
    {
        Unifont().IsStroked.ShouldBeTrue();
        Bigfont().IsStroked.ShouldBeTrue();
    }

    [Fact]
    public void StrokedOutline_TurnsThePenPathIntoClosedContours()
    {
        // What a fill rasterizer needs, and where the caller's width comes in: it lives in
        // the graphics state of whatever placed the text, never in the font.
        var open = new PathSink();
        var stroked = new PathSink();
        var font = Unifont();
        font.TryGetGlyph('I', open).ShouldBeTrue();
        font.TryGetStrokedOutline('I', stroked, strokeWidth: 1f).ShouldBeTrue();

        open.CloseCount.ShouldBe(0);
        stroked.CloseCount.ShouldBeGreaterThan(0);
        stroked.Points.Count.ShouldBeGreaterThan(open.Points.Count);

        // A 1-unit stroke on a bar from (0,0) to (0,8) spans x in [-0.5, 0.5].
        stroked.Points.Min(p => p.X).ShouldBe(-0.5f, tolerance: 0.05f);
        stroked.Points.Max(p => p.X).ShouldBe(0.5f, tolerance: 0.05f);
    }

    [Theory]
    [InlineData('I')]
    [InlineData('O')]
    [InlineData('A')]
    [InlineData('Z')]
    public void RenderGlyph_ProducesAnAntiAliasedBitmap(char ch)
    {
        var bmp = Unifont().RenderGlyph(ch, pixelsPerEm: 64f, strokeWidth: 0.6f);
        bmp.IsEmpty.ShouldBeFalse();
        bmp.Width.ShouldBeGreaterThan(0);
        bmp.Height.ShouldBeGreaterThan(0);
        bmp.Alpha.Max().ShouldBe((byte)255);
    }

    [Fact]
    public void RenderGlyph_WithoutAStroke_IsEmpty()
    {
        // A zero width has no area, which is the honest answer rather than a hairline guess.
        Unifont().RenderGlyph('I', 64f, strokeWidth: 0f).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ZeroWidthAndZeroHeightGlyphs_BothRender()
    {
        // 'I' has zero width and '-' zero height. Per-axis normalisation divides by one of
        // them; stroking is what gives each an extent at all.
        Unifont().RenderGlyph('I', 64f, 0.6f).IsEmpty.ShouldBeFalse();
        Unifont().RenderGlyph('-', 64f, 0.6f).IsEmpty.ShouldBeFalse();
    }

    // ------------------------------------------------------------------- helpers

    private static string Trace(ShxFont font, int code,
        ShxTextOrientation orientation = ShxTextOrientation.Horizontal)
    {
        var sink = new PathSink();
        font.TryGetGlyph(code, sink, orientation).ShouldBeTrue();
        return Format(sink);
    }

    private static string Format(PathSink sink)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sink.Ops.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            var (x, y) = sink.Points[i];
            sb.Append(sink.Ops[i]).Append('(')
              .Append(Num(x)).Append(',').Append(Num(y)).Append(')');
        }
        return sb.ToString();
    }

    private static string Num(float v) =>
        MathF.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// A one-glyph unifont at code 'X' wrapping <paramref name="opcodes"/>, for exercising
    /// opcodes the bundled fixtures do not reach. Metrics match the unifont fixture.
    /// </summary>
    private static ShxFont Synth(params byte[] opcodes)
    {
        var fontDef = Encoding.ASCII.GetBytes("SYNTH\0").Concat(new byte[] { 8, 2, 0, 0, 0, 0 })
            .ToArray();
        var record = new byte[] { 0x00 }.Concat(opcodes).Concat([(byte)0x00]).ToArray();

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("AutoCAD-86 unifont 1.0\r\n\x1a"));
        bytes.AddRange([2, 0, 0, 0]);                                  // count, incl. the definition
        bytes.AddRange([(byte)fontDef.Length, 0]);
        bytes.AddRange(fontDef);
        bytes.AddRange([(byte)'X', 0]);                                 // code
        bytes.AddRange([(byte)record.Length, 0]);
        bytes.AddRange(record);
        return ShxFont.Load(bytes.ToArray());
    }

    private sealed class PathSink : IGlyphSink
    {
        public readonly List<char> Ops = [];
        public readonly List<(float X, float Y)> Points = [];
        public int CloseCount;
        public int LineCount => Ops.Count(o => o == 'L');
        public int CubicCount => Ops.Count(o => o == 'C');

        public void MoveTo(float x, float y) { Ops.Add('M'); Points.Add((x, y)); }
        public void LineTo(float x, float y) { Ops.Add('L'); Points.Add((x, y)); }
        public void QuadTo(float cx, float cy, float x, float y) { Ops.Add('Q'); Points.Add((x, y)); }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        { Ops.Add('C'); Points.Add((x, y)); }
        public void Close() => CloseCount++;
    }
}
