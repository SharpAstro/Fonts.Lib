using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Tests;

public class GlyphOutlineTests
{
    [Fact]
    public void DejaVuSans_HasGlyfTable()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Glyf.ShouldNotBeNull();
        font.Loca.ShouldNotBeNull();
        font.Hmtx.ShouldNotBeNull();
    }

    [Fact]
    public void DejaVuSans_GlyphZero_LoadsWithoutThrowing()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var outline = font.LoadGlyphOutline(0);
        // .notdef is conventionally a rectangle — at least one contour.
        outline.IsEmpty.ShouldBeFalse();
        outline.ContourCount.ShouldBeGreaterThan(0);
        outline.PointCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void DejaVuSans_LetterA_HasReasonableOutline()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId('A');
        var outline = font.LoadGlyphOutline(gid);

        outline.ContourCount.ShouldBe(2);     // outer hull + inner counter
        outline.PointCount.ShouldBeGreaterThan(8);
        outline.Bounds.XMax.ShouldBeGreaterThan(outline.Bounds.XMin);
        outline.Bounds.YMax.ShouldBeGreaterThan(outline.Bounds.YMin);

        // X/Y/Flags arrays must be in lockstep.
        outline.X.Length.ShouldBe(outline.PointCount);
        outline.Y.Length.ShouldBe(outline.PointCount);
        outline.Flags.Length.ShouldBe(outline.PointCount);
    }

    [Fact]
    public void DejaVuSans_Space_IsEmpty()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId(' ');
        var outline = font.LoadGlyphOutline(gid);
        outline.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void DejaVuSans_AdvanceWidth_IsPositiveForLetters()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var hmtx = font.Hmtx.ShouldNotBeNull();
        foreach (var ch in "ABCxyz0987 ")
        {
            var gid = font.GetGlyphId(ch);
            hmtx.GetAdvanceWidth(gid).ShouldBeGreaterThan((ushort)0);
        }
    }

    [Fact]
    public void DejaVuSans_AllGlyphs_LoadWithoutThrowing()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var totalPoints = 0;
        for (uint i = 0; i < font.NumGlyphs; i++)
        {
            var o = font.LoadGlyphOutline(i);
            totalPoints += o.PointCount;
        }
        // Sanity: a real font has *some* points.
        totalPoints.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Composite_eAcute_HasMoreContoursThan_e()
    {
        // U+00E9 (é) is composite in most fonts: e + combining acute.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var e = font.LoadGlyphOutline(font.GetGlyphId('e'));
        var eAcute = font.LoadGlyphOutline(font.GetGlyphId('é'));

        eAcute.ContourCount.ShouldBeGreaterThanOrEqualTo(e.ContourCount);
        eAcute.PointCount.ShouldBeGreaterThan(e.PointCount);
    }

    [Fact]
    public void BezierFlattener_VisitsAllContours()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var outline = font.LoadGlyphOutline(font.GetGlyphId('B'));

        var sink = new CountingSink();
        BezierFlattener.Walk(outline, sink);

        sink.MoveCount.ShouldBe(outline.ContourCount);
        sink.CloseCount.ShouldBe(outline.ContourCount);
        // Should produce at least one drawing op per contour.
        (sink.LineCount + sink.QuadCount).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OpenTypeFont_IsConcurrentlyReadable()
    {
        // Smoke test: hammering a single shared font from many threads should
        // never throw or produce inconsistent outlines.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId('A');
        var expected = font.LoadGlyphOutline(gid);

        Parallel.For(0, 256, _ =>
        {
            var actual = font.LoadGlyphOutline(gid);
            actual.PointCount.ShouldBe(expected.PointCount);
            actual.ContourCount.ShouldBe(expected.ContourCount);
        });
    }

    private sealed class CountingSink : IGlyphSink
    {
        public int MoveCount { get; private set; }
        public int LineCount { get; private set; }
        public int QuadCount { get; private set; }
        public int CloseCount { get; private set; }
        public void MoveTo(float x, float y) => MoveCount++;
        public void LineTo(float x, float y) => LineCount++;
        public void QuadTo(float cx, float cy, float x, float y) => QuadCount++;
        public void Close() => CloseCount++;
    }
}
