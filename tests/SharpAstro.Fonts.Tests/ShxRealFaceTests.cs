using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Shx;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Opt-in validation against real <c>.shx</c> faces, which cannot be bundled: Autodesk's
/// stock fonts are their intellectual property and this repository is MIT end to end.
///
/// <para>Point <c>SHX_TEST_FONT_DIR</c> at a local directory of <c>.shx</c> files and these
/// run; leave it unset and they skip. The bundled synthetic fixtures are what CI checks —
/// they are the stronger correctness fixture anyway, since their geometry is known exactly —
/// while these are the breadth check: a real corpus is full of truncated records, damaged
/// index offsets and files that are not SHX at all despite the extension, and none of that
/// may throw.</para>
/// </summary>
public class ShxRealFaceTests
{
    private const string DirVariable = "SHX_TEST_FONT_DIR";

    private static string[] Corpus()
    {
        var dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            Assert.Skip($"Set {DirVariable} to a directory of .shx files to run this.");
        return Directory.GetFiles(dir!, "*.shx", SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Load every file and classify it. Nothing may throw anything other than the two
    /// documented exceptions, and every text font that loads must yield usable geometry.
    /// </summary>
    [Fact]
    public void EveryFile_EitherLoadsAsAFont_OrIsRejectedForAStatedReason()
    {
        var files = Corpus();
        files.ShouldNotBeEmpty();

        int unifont = 0, bigfont = 0, shapes = 0, notShx = 0;
        var unexpected = new List<string>();
        var empty = new List<string>();

        foreach (var path in files)
        {
            try
            {
                var font = ShxFont.Load(File.ReadAllBytes(path));
                if (font.Format == ShxFormat.Unifont) unifont++; else bigfont++;
                font.Name.ShouldNotBeNull();

                // A font with no codes is legitimate -- a placeholder whose index entries are
                // all zeroed -- but misreading the record table would empty them wholesale, so
                // the count is what is asserted rather than the individual case.
                if (font.Codes.IsEmpty) empty.Add(Path.GetFileName(path));
            }
            catch (NotSupportedException) { shapes++; }        // a shape library, refused by header
            catch (InvalidDataException) { notShx++; }         // not an SHX file at all
#pragma warning disable CA1031                                  // the point is that nothing else escapes
            catch (Exception ex)
#pragma warning restore CA1031
            {
                unexpected.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        unexpected.ShouldBeEmpty();
        (unifont + bigfont).ShouldBeGreaterThan(0);
        empty.Count.ShouldBeLessThan((unifont + bigfont) / 20,
            $"{empty.Count} fonts loaded with no codes: {string.Join(", ", empty.Take(10))}");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{files.Length} files: {unifont} unifont, {bigfont} bigfont, " +
            $"{shapes} shape libraries refused, {notShx} not SHX, {empty.Count} empty.");
    }

    /// <summary>
    /// Decode every glyph of every text font. Real faces contain truncated records, so the
    /// interpreter has to bounds-check every operand rather than trust the stream: an
    /// unguarded read throws partway through an otherwise perfectly usable font.
    /// </summary>
    [Fact]
    public void EveryGlyph_DecodesWithoutThrowing_AndStaysFinite()
    {
        var files = Corpus();
        long glyphs = 0, drawn = 0, commands = 0;
        var failures = new List<string>();

        foreach (var path in files)
        {
            ShxFont font;
            try { font = ShxFont.Load(File.ReadAllBytes(path)); }
            catch (NotSupportedException) { continue; }
            catch (InvalidDataException) { continue; }

            foreach (var code in font.Codes)
            {
                glyphs++;
                var sink = new BoundsSink();
                try
                {
                    font.TryGetGlyph(code, sink).ShouldBeTrue();
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failures.Add($"{Path.GetFileName(path)} U+{code:X4}: {ex.GetType().Name}");
                    continue;
                }

                commands += sink.Count;
                if (sink.Count == 0) continue;
                drawn++;

                // No NaN or infinity may reach a consumer: the arc maths divides by a sagitta
                // and by a radius, either of which a damaged face can present as zero.
                sink.Finite.ShouldBeTrue($"{Path.GetFileName(path)} U+{code:X4} emitted a non-finite point");
            }
        }

        failures.ShouldBeEmpty();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{glyphs} glyphs, {drawn} with geometry, {commands} path commands.");

        // A decoder that misses the leading NUL-terminated glyph name returns an empty glyph
        // for every character while the font still loads cleanly. That failure is invisible
        // unless something asserts the fonts actually draw.
        (drawn > glyphs * 0.5).ShouldBeTrue($"only {drawn} of {glyphs} glyphs produced geometry");
    }

    /// <summary>
    /// Arcs are load-bearing, and skipping them fails in a way that is easy to miss: it
    /// loses exactly the round glyphs. Assert that round characters in faces that use arcs
    /// actually curve.
    /// </summary>
    [Fact]
    public void RoundGlyphs_InArcUsingFaces_EmitCurves()
    {
        var files = Corpus();
        var arcFaces = 0;

        foreach (var path in files)
        {
            ShxFont font;
            try { font = ShxFont.Load(File.ReadAllBytes(path)); }
            catch (NotSupportedException) { continue; }
            catch (InvalidDataException) { continue; }
            if (font.Format != ShxFormat.Unifont) continue;

            // Only faces that draw 'O' and 'D' with curves at all -- txt.shx draws its D with
            // six straight segments and would be a false failure.
            var o = new BoundsSink();
            var d = new BoundsSink();
            if (!font.TryGetGlyph('O', o) || !font.TryGetGlyph('D', d)) continue;
            if (o.Cubics == 0 && d.Cubics == 0) continue;

            arcFaces++;
            (o.Cubics + d.Cubics).ShouldBeGreaterThan(0);
            o.Count.ShouldBeGreaterThan(0);
        }

        arcFaces.ShouldBeGreaterThan(0, "no face in the corpus produced a curved O or D");
        TestContext.Current.TestOutputHelper?.WriteLine($"{arcFaces} faces draw round glyphs with arcs.");
    }

    /// <summary>
    /// Glyph geometry should sit within a sane multiple of the em. This is the blunt check
    /// that the scale commands, arc radii and subshape composition are not producing
    /// runaway coordinates.
    /// </summary>
    [Fact]
    public void GlyphBounds_StayWithinASaneMultipleOfTheEm()
    {
        var files = Corpus();
        var wild = new List<string>();
        long checkedGlyphs = 0;

        foreach (var path in files)
        {
            ShxFont font;
            try { font = ShxFont.Load(File.ReadAllBytes(path)); }
            catch (NotSupportedException) { continue; }
            catch (InvalidDataException) { continue; }

            var em = font.UnitsPerEm;
            if (em <= 0) continue;
            var limit = em * 20f;

            foreach (var code in font.Codes)
            {
                var sink = new BoundsSink();
                font.TryGetGlyph(code, sink);
                if (sink.Count == 0) continue;
                checkedGlyphs++;
                var extent = Math.Max(
                    Math.Max(Math.Abs(sink.MinX), Math.Abs(sink.MaxX)),
                    Math.Max(Math.Abs(sink.MinY), Math.Abs(sink.MaxY)));
                if (extent > limit)
                    wild.Add($"{Path.GetFileName(path)} U+{code:X4} extent {extent:F0} vs em {em}");
            }
        }

        checkedGlyphs.ShouldBeGreaterThan(0);
        // A handful of genuinely damaged faces is expected; a systematic decode error is not.
        var ratio = wild.Count / (double)checkedGlyphs;
        ratio.ShouldBeLessThan(0.01,
            $"{wild.Count} of {checkedGlyphs} glyphs exceed 20x the em, e.g. {string.Join("; ", wild.Take(5))}");
    }

    /// <summary>
    /// Render a contact sheet of the canonical stock faces to <c>ShxDumps/</c> under the test
    /// output directory. Not an assertion so much as the thing you look at when a decode goes
    /// subtly wrong: bad arc centres, mirrored octants and a wrong minor-axis constant all
    /// produce geometry that passes every numeric check and is obviously broken on sight.
    /// </summary>
    [Fact]
    public void ContactSheet_OfStockFaces_ForVisualInspection()
    {
        var files = Corpus();
        var dir = Path.Combine(AppContext.BaseDirectory, "ShxDumps");
        Directory.CreateDirectory(dir);

        const string Sample = "ABDOQRSbcdefgo28@";
        var wanted = new[] { "romans", "isocp", "txt", "romanc", "italic", "gothice" };
        var rendered = 0;

        foreach (var face in wanted)
        {
            var path = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), face,
                    StringComparison.OrdinalIgnoreCase));
            if (path is null) continue;

            var font = ShxFont.Load(File.ReadAllBytes(path));
            if (font.UnitsPerEm <= 0) continue;

            const int Cell = 64;
            var sheet = new byte[Sample.Length * Cell * Cell * 4];
            for (var i = 0; i < Sample.Length; i++)
            {
                // A pen width of about 1/14 of the em is roughly how AutoCAD plots these.
                var bmp = font.RenderGlyph(Sample[i], Cell * 0.7f, font.UnitsPerEm / 14f);
                if (bmp.IsEmpty) continue;
                Blit(sheet, Sample.Length * Cell, Cell, bmp, i * Cell + 8, Cell - 12 - bmp.Top);
            }
            PngWriter.WriteRgba(Path.Combine(dir, $"{face}.png"), sheet,
                Sample.Length * Cell, Cell);
            rendered++;
        }

        rendered.ShouldBeGreaterThan(0);
        TestContext.Current.TestOutputHelper?.WriteLine($"Wrote {rendered} contact sheets to {dir}");
    }

    private static void Blit(byte[] rgba, int sheetWidth, int sheetHeight, GlyphBitmap bmp,
        int x0, int y0)
    {
        for (var y = 0; y < bmp.Height; y++)
        {
            var ty = y0 + y;
            if (ty < 0 || ty >= sheetHeight) continue;
            for (var x = 0; x < bmp.Width; x++)
            {
                var tx = x0 + bmp.Left + x;
                if (tx < 0 || tx >= sheetWidth) continue;
                var a = bmp.Alpha[y * bmp.Width + x];
                if (a == 0) continue;
                var o = (ty * sheetWidth + tx) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)(255 - a);
                rgba[o + 3] = 255;
            }
        }
    }

    private sealed class BoundsSink : IGlyphSink
    {
        public int Count;
        public int Cubics;
        public bool Finite = true;
        public float MinX = float.MaxValue, MinY = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue;

        private void Add(float x, float y)
        {
            Count++;
            if (!float.IsFinite(x) || !float.IsFinite(y)) { Finite = false; return; }
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public void MoveTo(float x, float y) => Add(x, y);
        public void LineTo(float x, float y) => Add(x, y);
        public void QuadTo(float cx, float cy, float x, float y) => Add(x, y);
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Cubics++;
            Add(c1x, c1y);
            Add(c2x, c2y);
            Add(x, y);
        }
        public void Close() { }
    }
}
