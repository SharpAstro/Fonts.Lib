using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The layout of a scene's text (<see cref="SceneText"/>), in numbers worked out from the font's own tables: where
/// each character ends up, not what it looks like. The pictures as a whole are held against a browser in
/// <see cref="SceneTextBrowserTests"/>.
/// </summary>
public sealed class SceneTextTests
{
    private static readonly Lazy<OpenTypeFont> DejaVu = new(() => OpenTypeFont.Load(File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans))));

    private static OpenTypeFont Font => DejaVu.Value;

    private static SceneTextShaper Shaper => SceneText.ShaperFor(Font);

    /// <summary>The advance of a character at a size, from 'hmtx'.</summary>
    private static float Advance(char character, float size) =>
        Font.Hmtx!.GetAdvanceWidth(Font.GetGlyphId(character)) * size / Font.UnitsPerEm;

    private static float Width(string text, float size) => text.Sum(c => Advance(c, size));

    private static VectorText Text(string body)
    {
        SvgDocument document = SvgReader.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\">{body}</svg>");
        document.Issues.ShouldBeEmpty();
        return document.Scene.Nodes.OfType<VectorText>().ShouldHaveSingleItem();
    }

    private static VectorShape[] Shapes(string body) => [.. SceneText.ToShapes(Text(body), Shaper).Cast<VectorShape>()];

    private static VectorRect Box(VectorShape shape)
    {
        shape.Path.TryGetBounds(out VectorRect box).ShouldBeTrue();
        return box;
    }

    // "H" is a letter of straight stems that stands on the baseline and reaches the cap height: its ink box says where it is.
    private static float CapHeight(float size) => Box(Shapes($"<text font-size=\"{size}\">H</text>")[0]).Height;

    private static float LeftBearing(char character, float size) =>
        Font.Hmtx!.GetLeftSideBearing(Font.GetGlyphId(character)) * size / Font.UnitsPerEm;

    // ---- Drawing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAShaper_TextIsNotDrawn_AndWithOneItIs()
    {
        VectorScene scene = SvgReader.Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\"><text x=\"5\" y=\"30\" font-size=\"30\" fill=\"#c02040\">Hi</text></svg>").Scene;
        // No font is known to the rasteriser: it leaves the text out and does not fail.
        SceneRasterizer.Rasterize(scene, 100, 40).Pixels.ShouldAllBe(b => b == 0);
        SceneRasterizer.Rasterize(scene, 100, 40, Matrix3x2.Identity, new SceneRenderOptions()).Pixels.ShouldAllBe(b => b == 0);

        ColorBitmap drawn = SceneRasterizer.Rasterize(scene, 100, 40, Matrix3x2.Identity, new SceneRenderOptions { ShapeText = Shaper });
        // The left stem of the H: solid, in the text's colour.
        int x = (int)(5 + LeftBearing('H', 30) + 1), at = (20 * 100 + x) * 4;
        drawn.Pixels.AsSpan(at, 4).ToArray().ShouldBe([(byte)0xC0, (byte)0x20, (byte)0x40, (byte)255]);
        SceneRasterizer.RasterizeCoverage(scene, 100, 40, Matrix3x2.Identity, new SceneRenderOptions { ShapeText = Shaper })[20 * 100 + x].ShouldBe((byte)255);
    }

    [Fact]
    public void TheWholeSceneCanBeTurnedIntoShapes_InGroupsMasksAndTilesToo()
    {
        VectorScene scene = SvgReader.Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><defs>" +
            "<mask id=\"m\" maskUnits=\"userSpaceOnUse\" width=\"100\" height=\"100\"><text y=\"20\" fill=\"white\">M</text></mask>" +
            "<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"20\" height=\"20\"><text y=\"15\">P</text></pattern></defs>" +
            "<g opacity=\"0.5\"><text y=\"20\">G</text><rect width=\"9\" height=\"9\"/></g>" +
            "<rect width=\"50\" height=\"50\" mask=\"url(#m)\" fill=\"url(#p)\"/><text y=\"90\">T</text></svg>").Scene;
        CountText(scene.Nodes).ShouldBe(4);

        VectorScene outlined = SceneText.WithTextAsShapes(scene, Shaper);
        CountText(outlined.Nodes).ShouldBe(0);
        (outlined.Width, outlined.Height).ShouldBe((scene.Width, scene.Height));
        // And it draws the same with no shaper at all as the scene with text does with one.
        SceneRasterizer.Rasterize(outlined, 100, 100).Pixels
            .ShouldBe(SceneRasterizer.Rasterize(scene, 100, 100, Matrix3x2.Identity, new SceneRenderOptions { ShapeText = Shaper }).Pixels);

        static int CountText(ImmutableArray<VectorNode> nodes) => nodes.Sum(node => node switch
        {
            VectorText => 1,
            VectorGroup group => CountText(group.Children) + (group.Mask is { } mask ? CountText(mask.Children) : 0),
            VectorShape { Fill.Paint: PatternPaint pattern } => CountText(pattern.Children),
            _ => 0,
        });
    }

    // ---- Where characters go ----------------------------------------------------------------------------------------

    [Fact]
    public void ARunStartsAtItsPlace_OnTheBaseline_AndEachCharacterFollowsTheLast()
    {
        VectorShape shape = Shapes("<text x=\"20\" y=\"100\" font-size=\"40\" transform=\"translate(3 4)\">HH</text>").ShouldHaveSingleItem();
        shape.Transform.ShouldBe(Matrix3x2.CreateTranslation(3, 4));
        VectorRect box = Box(shape);
        float bearing = LeftBearing('H', 40);
        box.X.ShouldBe(20 + bearing, 0.01f);
        box.Bottom.ShouldBe(100, 0.01f);                                    // standing on the baseline
        box.Y.ShouldBe(100 - CapHeight(40), 0.01f);
        // The second H is one advance on: the ink ends where its own would, an advance later.
        box.Right.ShouldBe(20 + Advance('H', 40) + bearing + (Box(Shapes("<text font-size=\"40\">H</text>")[0]).Width), 0.01f);
    }

    [Theory]
    [InlineData("start", 0f)]
    [InlineData("middle", 0.5f)]
    [InlineData("end", 1f)]
    public void AChunkIsMovedSoThatItsAnchorIsWhereItWasPut(string anchor, float share)
    {
        float width = Width("HHH", 30);
        VectorRect box = Box(Shapes($"<text x=\"200\" y=\"50\" font-size=\"30\" text-anchor=\"{anchor}\">HHH</text>")[0]);
        box.X.ShouldBe(200 - width * share + LeftBearing('H', 30), 0.01f);
    }

    [Fact]
    public void ACharacterWithAPlaceOfItsOwn_StartsAChunk_AnchoredByItself()
    {
        // Two chunks, each centred on its own x.
        VectorShape[] shapes = Shapes("<text y=\"50\" font-size=\"30\" text-anchor=\"middle\">HH<tspan x=\"300\" fill=\"red\">HHH</tspan></text>");
        shapes.Length.ShouldBe(2);
        float bearing = LeftBearing('H', 30);
        Box(shapes[0]).X.ShouldBe(0 - Width("HH", 30) / 2 + bearing, 0.01f);
        Box(shapes[1]).X.ShouldBe(300 - Width("HHH", 30) / 2 + bearing, 0.01f);
    }

    [Fact]
    public void ShiftsAddUp_AndAPlaceTakesTheShiftOfItsCharacterWithIt()
    {
        float advance = Advance('H', 20), bearing = LeftBearing('H', 20), ink = Box(Shapes("<text font-size=\"20\">H</text>")[0]).Width;
        // dx on the first character moves all of it; dx on the third moves the third and what follows; dy likewise.
        VectorRect box = Box(Shapes("<text x=\"10\" dx=\"5 0 7\" y=\"50\" dy=\"0 0 -6\" font-size=\"20\">HHH</text>")[0]);
        box.X.ShouldBe(10 + 5 + bearing, 0.01f);
        box.Right.ShouldBe(10 + 5 + 2 * advance + 7 + bearing + ink, 0.01f);
        box.Y.ShouldBe(50 - 6 - CapHeight(20), 0.01f);
        box.Bottom.ShouldBe(50, 0.01f);
    }

    [Fact]
    public void ACharacterIsTurnedAboutItsOwnOrigin_AndTheNextIsNotMovedByIt()
    {
        // Turned a quarter clockwise about (100, 100): the H that stood up from the baseline now lies to the right of it.
        VectorRect box = Box(Shapes("<text x=\"100\" y=\"100\" font-size=\"40\" rotate=\"90\">H</text>")[0]);
        float cap = CapHeight(40), bearing = LeftBearing('H', 40);
        box.X.ShouldBe(100, 0.01f);
        box.Right.ShouldBe(100 + cap, 0.01f);
        box.Y.ShouldBe(100 + bearing, 0.01f);

        // The second character is where it would be had the first not been turned.
        VectorRect two = Box(Shapes("<text x=\"100\" y=\"100\" font-size=\"40\" rotate=\"90 0\">HH</text>")[0]);
        two.Right.ShouldBe(MathF.Max(100 + cap, 100 + Advance('H', 40) + bearing + Box(Shapes("<text font-size=\"40\">H</text>")[0]).Width), 0.01f);
        two.Y.ShouldBe(100 - cap, 0.01f);
    }

    [Fact]
    public void SpacingIsAddedAfterEveryCharacter_AndAfterEverySpace()
    {
        float plain = Box(Shapes("<text font-size=\"20\">H H</text>")[0]).Width;
        Box(Shapes("<text font-size=\"20\" letter-spacing=\"3\">H H</text>")[0]).Width.ShouldBe(plain + 2 * 3, 0.01f);
        Box(Shapes("<text font-size=\"20\" word-spacing=\"10\">H H</text>")[0]).Width.ShouldBe(plain + 10, 0.01f);
        // Trailing spacing is part of the chunk: text anchored at its end stands that much left of it.
        Box(Shapes("<text x=\"100\" font-size=\"20\" letter-spacing=\"3\" text-anchor=\"end\">H</text>")[0]).X
            .ShouldBe(100 - Advance('H', 20) - 3 + LeftBearing('H', 20), 0.01f);
    }

    [Fact]
    public void AHiddenSpanTakesItsRoom()
    {
        VectorShape[] shapes = Shapes("<text font-size=\"20\">H<tspan visibility=\"hidden\">HHH</tspan>H</text>");
        shapes.Length.ShouldBe(2);
        Box(shapes[1]).X.ShouldBe(4 * Advance('H', 20) + LeftBearing('H', 20), 0.01f);
    }

    [Fact]
    public void TextLength_WidensTheGaps_OrStretchesEverything()
    {
        float advance = Advance('H', 20), bearing = LeftBearing('H', 20), ink = Box(Shapes("<text font-size=\"20\">H</text>")[0]).Width;
        // Spacing: the last character starts where it must for the text to be 100 long.
        VectorRect spaced = Box(Shapes("<text font-size=\"20\" textLength=\"100\">HHH</text>")[0]);
        spaced.Right.ShouldBe(100 - advance + bearing + ink, 0.01f);
        // Spacing and glyphs: everything is as much wider as the text is longer.
        VectorRect stretched = Box(Shapes("<text font-size=\"20\" textLength=\"100\" lengthAdjust=\"spacingAndGlyphs\">HHH</text>")[0]);
        float factor = 100 / (3 * advance);
        stretched.X.ShouldBe(bearing * factor, 0.01f);
        stretched.Right.ShouldBe((2 * advance + bearing + ink) * factor, 0.01f);
        stretched.Height.ShouldBe(CapHeight(20), 0.01f);
    }

    [Fact]
    public void KerningBetweenNeighbours_IsInTheAdvance()
    {
        int kern = Font.GetKerning(Font.GetGlyphId('A'), Font.GetGlyphId('V'));
        kern.ShouldBeLessThan(0, "DejaVu Sans kerns A and V together");
        // The V is where it would be alone, moved on by the A's advance less the kerning of the pair.
        float alone = Box(Shapes("<text font-size=\"100\">V</text>")[0]).Right;
        Box(Shapes("<text font-size=\"100\">AV</text>")[0]).Right.ShouldBe(Advance('A', 100) + kern * 100f / Font.UnitsPerEm + alone, 0.01f);
        // A pair with no kerning is not moved.
        Font.GetKerning(Font.GetGlyphId('H'), Font.GetGlyphId('H')).ShouldBe(0);
    }

    // ---- Baselines ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBaselineAskedFor_LiesOnTheY()
    {
        float size = 40, scale = size / Font.UnitsPerEm, ascent = Font.Hhea!.Ascender * scale, descent = -Font.Hhea.Descender * scale;
        float Bottom(string attributes) => Box(Shapes($"<text y=\"100\" font-size=\"{size}\" {attributes}>H</text>")[0]).Bottom;

        Bottom("").ShouldBe(100, 0.01f);
        Bottom("dominant-baseline=\"central\"").ShouldBe(100 + (ascent - descent) / 2, 0.01f);
        Bottom("dominant-baseline=\"hanging\"").ShouldBe(100 + ascent * 0.8f, 0.01f);
        Bottom("dominant-baseline=\"mathematical\"").ShouldBe(100 + ascent * 0.5f, 0.01f);
        Bottom("dominant-baseline=\"text-before-edge\"").ShouldBe(100 + ascent, 0.01f);
        Bottom("dominant-baseline=\"text-after-edge\"").ShouldBe(100 - descent, 0.01f);
        Bottom("dominant-baseline=\"ideographic\"").ShouldBe(100 - descent, 0.01f);
        // Half the height of an x: lower than the central line of this face, above the baseline.
        Bottom("dominant-baseline=\"middle\"").ShouldBeInRange(100 + size * 0.2f, 100 + size * 0.35f);
        // Raised by a length, and as a superscript by half a line of its own size.
        Bottom("baseline-shift=\"7\"").ShouldBe(93, 0.01f);
        Box(Shapes($"<text y=\"100\" font-size=\"{size}\">H<tspan baseline-shift=\"super\" font-size=\"20\">H</tspan></text>")[1]).Bottom
            .ShouldBe(100 - (ascent + descent) / 2 * (20 / size), 0.01f);
        Box(Shapes($"<text y=\"100\" font-size=\"{size}\">H<tspan baseline-shift=\"sub\" font-size=\"20\">H</tspan></text>")[1]).Bottom
            .ShouldBe(100 + (ascent + descent) / 2 * (20 / size), 0.01f);
    }

    // ---- Lines --------------------------------------------------------------------------------------------------------

    [Fact]
    public void LinesAreATenthOfTheSizeThick_WhereABrowserPutsThem_InThePaintOfWhoAskedForThem()
    {
        // Size 50: 5 thick. Underline 3 below the baseline (half its thickness, rounded up); overline ending on the
        // ascent taken to a whole unit; line-through centred a third of that ascent above the baseline.
        float ascent = MathF.Round(Font.Hhea!.Ascender * 50f / Font.UnitsPerEm), width = Width("HH", 50);
        VectorShape[] shapes = Shapes("<text y=\"100\" font-size=\"50\" fill=\"#102030\" text-decoration=\"underline overline line-through\">H<tspan fill=\"red\">H</tspan></text>");
        // Under and over the glyphs first, then the two spans, then the line through them.
        shapes.Length.ShouldBe(4);
        shapes.Select(shape => ((SolidPaint)shape.Fill!.Paint).Color.R).ShouldBe([(byte)0x10, (byte)0x10, (byte)255, (byte)0x10]);

        VectorRect underAndOver = Box(shapes[0]), through = Box(shapes[3]);
        shapes[0].Path.Verbs.Length.ShouldBe(10);                                   // two rectangles: one bar each, under both spans
        underAndOver.X.ShouldBe(0f, 0.001f);
        underAndOver.Right.ShouldBe(width, 0.001f);
        underAndOver.Bottom.ShouldBe(100 + 3 + 5, 0.001f);
        underAndOver.Y.ShouldBe(100 - ascent - 5, 0.001f);
        through.Y.ShouldBe(100 - ascent / 3 - 2.5f, 0.001f);
        through.Height.ShouldBe(5f, 0.001f);
        through.X.ShouldBe(0f, 0.001f);
        through.Right.ShouldBe(width, 0.001f);
    }

    [Fact]
    public void AnUnderlineIsBrokenWhereAGlyphCrossesIt()
    {
        // The tail of a g hangs through the underline; an H does not reach it.
        VectorShape plain = Shapes("<text y=\"100\" font-size=\"50\" text-decoration=\"underline\">HH</text>")[0];
        plain.Path.Verbs.Length.ShouldBe(5);                                         // one rectangle
        VectorShape broken = Shapes("<text y=\"100\" font-size=\"50\" text-decoration=\"underline\">HgH</text>")[0];
        broken.Path.Verbs.Length.ShouldBe(10);                                       // two, with a gap for the g
        VectorRect whole = Box(broken);
        whole.X.ShouldBe(0f, 0.01f);
        whole.Right.ShouldBe(Width("HgH", 50), 0.01f);
        // A line through text is never broken.
        Shapes("<text y=\"100\" font-size=\"50\" text-decoration=\"line-through\">HgH</text>")[^1].Path.Verbs.Length.ShouldBe(5);
    }

    // ---- Paints -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AGradientInTheUnitsOfTheTextsBox_IsPutOnTheBoxOfTheCharactersCells()
    {
        VectorShape shape = Shapes(
            "<defs><linearGradient id=\"g\"><stop stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient></defs>" +
            "<text x=\"10\" y=\"100\" font-size=\"40\" fill=\"url(#g)\">HH</text>")[0];
        var paint = shape.Fill!.Paint.ShouldBeOfType<LinearGradientPaint>();
        float scale = 40f / Font.UnitsPerEm;
        // The cells: from the first character's origin to the end of the last one's advance, from the ascent to the descent.
        paint.Transform.M31.ShouldBe(10, 0.01f);
        paint.Transform.M11.ShouldBe(Width("HH", 40), 0.01f);
        paint.Transform.M32.ShouldBe(100 - Font.Hhea!.Ascender * scale, 0.01f);
        paint.Transform.M22.ShouldBe((Font.Hhea.Ascender - Font.Hhea.Descender) * scale, 0.01f);
    }

    [Fact]
    public void TheStrokeFirst_IsTwoShapes_TheFillOnTop()
    {
        VectorShape[] shapes = Shapes("<text font-size=\"30\" fill=\"red\" stroke=\"blue\" stroke-width=\"4\" paint-order=\"stroke\">H</text>");
        shapes.Length.ShouldBe(2);
        (shapes[0].Fill, shapes[1].Stroke).ShouldBe((null, null));
        shapes[0].Stroke.ShouldNotBeNull();
        shapes[1].Fill.ShouldNotBeNull();
        Shapes("<text font-size=\"30\" fill=\"red\" stroke=\"blue\" stroke-width=\"4\">H</text>").ShouldHaveSingleItem().Stroke.ShouldNotBeNull();
    }

    // ---- Along a path -------------------------------------------------------------------------------------------------

    [Fact]
    public void OnAPath_TheMiddleOfACharacterIsAtItsDistanceAlongIt_TurnedToThePathsDirection()
    {
        // A path straight down from (100, 20): text along it reads downwards, its baseline on the path, its tops to the right.
        VectorRect box = Box(Shapes("<text font-size=\"40\"><textPath path=\"M 100 20 V 300\" startOffset=\"30\">H</textPath></text>")[0]);
        float cap = CapHeight(40), bearing = LeftBearing('H', 40);
        box.X.ShouldBe(100, 0.01f);
        box.Right.ShouldBe(100 + cap, 0.01f);
        box.Y.ShouldBe(20 + 30 + bearing, 0.01f);

        // What is off the end is not drawn: at 25 px a character, a path 60 long holds two.
        float advance = Advance('H', 30);
        int fit = (int)MathF.Floor((60 + advance / 2) / advance);
        VectorShape cut = Shapes("<text font-size=\"30\"><textPath path=\"M 0 50 H 60\">HHHHHHHH</textPath></text>")[0];
        Box(cut).Right.ShouldBeLessThan(fit * advance);
        Box(cut).Right.ShouldBeGreaterThan((fit - 1) * advance);
        // On the path's right side it runs the other way: from the end back, reading leftwards and upside down.
        VectorRect right = Box(Shapes("<text font-size=\"40\"><textPath path=\"M 0 100 H 300\" side=\"right\">H</textPath></text>")[0]);
        right.Right.ShouldBe(300 - LeftBearing('H', 40), 0.01f);
        right.Y.ShouldBe(100, 0.01f);
        // And text after the path goes on from the path's end.
        VectorShape[] after = Shapes("<text font-size=\"30\"><textPath path=\"M 0 50 H 60\">H</textPath><tspan fill=\"red\">H</tspan></text>");
        Box(after[1]).X.ShouldBe(60 + LeftBearing('H', 30), 0.01f);
        Box(after[1]).Bottom.ShouldBe(50, 0.01f);
    }

    // ---- The shaper ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ACharacterTheFaceDoesNotHave_IsAskedForAgain_AndNoFaceIsNoText()
    {
        OpenTypeFont other = OpenTypeFont.Load(File.ReadAllBytes(Fixtures.Path(Fixtures.NotoSansJP)));
        const char kanji = '日';
        Font.GetGlyphId(kanji).ShouldBe(0u);
        other.GetGlyphId(kanji).ShouldNotBe(0u);

        var asked = new List<Rune>();
        SceneTextShaper shaper = SceneText.ShaperFor((_, rune) =>
        {
            asked.Add(rune);
            return rune.Value == kanji ? other : Font;
        });
        IReadOnlyList<SceneGlyph> glyphs = shaper("H" + kanji + "H", new VectorFont([], 20));
        glyphs.Select(glyph => ReferenceEquals(glyph.Font, other)).ShouldBe([false, true, false]);
        glyphs.Select(glyph => glyph.Cluster).ShouldBe([0, 1, 2]);
        // The face of the first character serves until a character it lacks: asked once for the H, once for the kanji.
        asked.Select(rune => rune.Value).ShouldBe(['H', kanji]);

        SceneText.ToShapes(Text("<text>HH</text>"), SceneText.ShaperFor((_, _) => null)).ShouldBeEmpty();
    }

    [Fact]
    public void ACallersShaper_IsTakenAtItsWord_ClustersAndOffsetsIncluded()
    {
        // One glyph for two characters (a ligature), then a glyph raised and moved by the shaper, in font units.
        uint h = Font.GetGlyphId('H');
        SceneTextShaper ligature = (text, _) =>
        [
            new SceneGlyph(Font, h, 0, 1000),
            new SceneGlyph(Font, h, 2, 1000, OffsetX: 200, OffsetY: 500),
        ];
        // The second character of the ligature has a dx: it adds to the shift of what follows, and places nothing.
        VectorText text = Text("<text font-size=\"2048\" dx=\"0 300 0\">abc</text>");
        VectorRect box = Box(SceneText.ToShapes(text, ligature).Cast<VectorShape>().Single());
        float bearing = Font.Hmtx!.GetLeftSideBearing(h), ink = Box(Shapes("<text font-size=\"2048\">H</text>")[0]).Width, cap = CapHeight(2048);
        box.X.ShouldBe(300 + bearing, 0.5f);                               // the ligature takes the shift of both its characters
        box.Right.ShouldBe(300 + 1000 + 200 + bearing + ink, 0.5f);
        box.Y.ShouldBe(-500 - cap, 0.5f);
    }
}
