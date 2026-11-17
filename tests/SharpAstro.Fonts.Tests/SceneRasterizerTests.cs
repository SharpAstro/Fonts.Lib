using System.Numerics;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The scene rasteriser against things that can be worked out by hand: areas, exact pixels, what overlaps what.
/// <see cref="SceneRasterizerBrowserTests"/> holds it against a browser.
/// </summary>
public sealed class SceneRasterizerTests
{
    private static VectorScene Scene(string body, int width = 40, int height = 40) =>
        SvgReader.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\">{body}</svg>").Scene;

    private static ColorBitmap Draw(string body, int width = 40, int height = 40) => SceneRasterizer.Rasterize(Scene(body, width, height), width, height);

    private static (byte R, byte G, byte B, byte A) At(ColorBitmap bitmap, int x, int y)
    {
        int i = (y * bitmap.Width + x) * 4;
        return (bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    /// <summary>The picture's ink in pixels: the sum of its alpha.</summary>
    private static double Area(ColorBitmap bitmap)
    {
        double sum = 0;
        for (int i = 3; i < bitmap.Pixels.Length; i += 4) sum += bitmap.Pixels[i];
        return sum / 255.0;
    }

    // ---- Clips, masks, images, patterns (what SharpAstro.Svg 3.37 added to the scene) ---------------------------------

    [Fact]
    public void AClip_KeepsOnlyWhatIsInsideIt_AndTwoOutlinesSideBySideLeaveNoSeam()
    {
        ColorBitmap bitmap = Draw("<clipPath id=\"c\"><rect x=\"10\" y=\"10\" width=\"10\" height=\"10\"/></clipPath>" +
            "<rect width=\"40\" height=\"40\" fill=\"#c06030\" clip-path=\"url(#c)\"/>");
        Area(bitmap).ShouldBe(100, 1e-6);
        At(bitmap, 10, 10).ShouldBe(((byte)0xc0, (byte)0x60, (byte)0x30, (byte)255));
        At(bitmap, 9, 10).A.ShouldBe((byte)0);
        At(bitmap, 20, 19).A.ShouldBe((byte)0);

        // Two outlines that share an edge in the middle of a pixel column: that column is whole, not three quarters.
        bitmap = Draw("<clipPath id=\"c\"><rect x=\"10\" y=\"10\" width=\"5.5\" height=\"10\"/><rect x=\"15.5\" y=\"10\" width=\"4.5\" height=\"10\"/></clipPath>" +
            "<rect width=\"40\" height=\"40\" clip-path=\"url(#c)\"/>");
        At(bitmap, 15, 15).A.ShouldBe((byte)255);
        Area(bitmap).ShouldBe(100, 1e-3);

        // A clip on one outline, and a clip the whole is intersected with.
        bitmap = Draw("<clipPath id=\"c\" clip-path=\"url(#top)\"><rect x=\"0\" y=\"0\" width=\"20\" height=\"40\" clip-path=\"url(#left)\"/><rect x=\"30\" width=\"10\" height=\"40\"/></clipPath>" +
            "<clipPath id=\"left\"><rect width=\"10\" height=\"40\"/></clipPath><clipPath id=\"top\"><rect width=\"40\" height=\"5\"/></clipPath>" +
            "<rect width=\"40\" height=\"40\" clip-path=\"url(#c)\"/>");
        Area(bitmap).ShouldBe(10 * 5 + 10 * 5, 1e-6);
    }

    [Theory]
    // Luminance: 0.2125 R + 0.7154 G + 0.0721 B of the sRGB values, times the mask's alpha.
    [InlineData("", "#fff", 1, 255)]
    [InlineData("", "#000", 1, 0)]
    [InlineData("", "#f00", 1, 54)]
    [InlineData("", "#0f0", 1, 182)]
    [InlineData("", "#fff", 0.5, 128)]
    // Alpha: the colour does not matter.
    [InlineData("mask-type=\"alpha\"", "#000", 0.5, 128)]
    [InlineData("mask-type=\"alpha\"", "#f00", 1, 255)]
    public void AMask_ScalesTheLayerByItsPicture(string attributes, string fill, double opacity, int alpha)
    {
        ColorBitmap bitmap = Draw($"<mask id=\"m\" {attributes}><rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"{fill}\" fill-opacity=\"{opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/></mask>" +
            "<rect width=\"40\" height=\"40\" fill=\"#102030\" mask=\"url(#m)\"/>");
        ((int)At(bitmap, 15, 15).A).ShouldBeInRange(alpha - 1, alpha + 1);
        // Outside the mask's picture nothing is kept.
        At(bitmap, 25, 25).A.ShouldBe((byte)0);
        if (alpha > 0) At(bitmap, 15, 15).B.ShouldBe((byte)0x30);
    }

    /// <summary>A 2 x 2 picture: red, green / blue, half-transparent white.</summary>
    private static readonly byte[] FourPixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128];

    private static string Png() => "data:image/png;base64," + Convert.ToBase64String(SharpAstro.Png.PngWriter.Encode(FourPixels, 2, 2));

    [Fact]
    public void AnEmbeddedPng_IsDecodedAndFittedIntoItsBox()
    {
        // 2 x 2 into 20 x 20 at (10, 10): each of its pixels is a 10 x 10 block, blended only near where two meet.
        ColorBitmap bitmap = Draw($"<image x=\"10\" y=\"10\" width=\"20\" height=\"20\" href=\"{Png()}\"/>");
        At(bitmap, 11, 11).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));
        At(bitmap, 28, 11).ShouldBe(((byte)0, (byte)255, (byte)0, (byte)255));
        At(bitmap, 11, 28).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));
        At(bitmap, 28, 28).ShouldBe(((byte)255, (byte)255, (byte)255, (byte)128));
        At(bitmap, 9, 15).A.ShouldBe((byte)0);
        At(bitmap, 30, 15).A.ShouldBe((byte)0);

        // Meet: a 2 x 2 picture in a 20 x 10 box is 10 x 10, centred. Slice: 20 x 20, of which the box shows the middle.
        Area(Draw($"<image x=\"10\" y=\"10\" width=\"20\" height=\"10\" href=\"{Png()}\" opacity=\"1\"/>")).ShouldBe(75 + 25 * 128 / 255.0, 0.5);
        ColorBitmap sliced = Draw($"<image x=\"10\" y=\"10\" width=\"20\" height=\"10\" preserveAspectRatio=\"xMidYMin slice\" href=\"{Png()}\"/>");
        At(sliced, 11, 11).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));
        At(sliced, 15, 20).A.ShouldBe((byte)0);
        At(sliced, 28, 19).G.ShouldBe((byte)255);
    }

    [Fact]
    public void ThePicturesDecoder_IsTheCallers_AskedOnceAPicture_AndAPictureNobodyDecodesIsNotDrawn()
    {
        VectorScene scene = Scene("<defs><image id=\"i\" width=\"10\" height=\"10\" href=\"https://203.0.113.1/never.jpg\"/></defs>" +
            "<use href=\"#i\"/><use href=\"#i\" x=\"20\"/><rect y=\"30\" width=\"5\" height=\"5\"/>");

        // By default only an embedded PNG is decoded: a picture that is only named is left out, and nothing is fetched.
        Area(SceneRasterizer.Rasterize(scene, 40, 40)).ShouldBe(25, 1e-6);
        SceneRasterizer.DecodePng(scene.Nodes.OfType<VectorImage>().First()).ShouldBeNull();

        var asked = new List<string?>();
        ColorBitmap bitmap = SceneRasterizer.Rasterize(scene, 40, 40, Matrix3x2.Identity, image =>
        {
            asked.Add(image.Source);
            return new ColorBitmap([10, 20, 30, 255], 1, 1, 0, 0);
        });
        // Two nodes, but they are two instances of one picture only by address: each node is asked for once.
        asked.ShouldBe(["https://203.0.113.1/never.jpg", "https://203.0.113.1/never.jpg"]);
        At(bitmap, 5, 5).ShouldBe(((byte)10, (byte)20, (byte)30, (byte)255));
        At(bitmap, 25, 5).ShouldBe(((byte)10, (byte)20, (byte)30, (byte)255));
        Area(bitmap).ShouldBe(225, 1e-6);

        // What a decoder returns is not trusted to be whole.
        Area(SceneRasterizer.Rasterize(scene, 40, 40, Matrix3x2.Identity, _ => new ColorBitmap([1, 2, 3], 4, 4, 0, 0))).ShouldBe(25, 1e-6);
    }

    [Fact]
    public void APattern_RepeatsItsTile_AndWhatTheTileDrawsOutsideItselfIsCutOff()
    {
        // An 8 x 8 tile with a 4 x 4 square in its corner, and a square that sticks out of the tile to the right.
        ColorBitmap bitmap = Draw("<pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\"><rect width=\"4\" height=\"4\" fill=\"#c06030\"/>" +
            "<rect x=\"6\" y=\"6\" width=\"6\" height=\"2\" fill=\"#0000ff\"/></pattern><rect width=\"40\" height=\"40\" fill=\"url(#p)\"/>");
        foreach ((int x, int y) in ((int, int)[])[(1, 1), (9, 1), (17, 25), (33, 33)])
        {
            At(bitmap, x, y).ShouldBe(((byte)0xc0, (byte)0x60, (byte)0x30, (byte)255), $"{x},{y}");
        }
        At(bitmap, 5, 5).A.ShouldBe((byte)0);
        At(bitmap, 7, 7).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));
        // The part of the blue square beyond the tile's edge is not drawn into the next tile.
        At(bitmap, 9, 7).A.ShouldBe((byte)0);
        // 25 tiles, each 16 + 4 pixels of ink.
        Area(bitmap).ShouldBe(25 * 20, 1);
    }

    [Fact]
    public void ARectangleOnPixelEdges_IsExact_AndHalfAPixelIsHalfCovered()
    {
        ColorBitmap bitmap = Draw("<rect x=\"10\" y=\"5\" width=\"20\" height=\"10\" fill=\"#c06030\"/>");
        (bitmap.Width, bitmap.Height, bitmap.Left, bitmap.Top).ShouldBe((40, 40, 0, 0));
        At(bitmap, 10, 5).ShouldBe(((byte)0xc0, (byte)0x60, (byte)0x30, (byte)255));
        At(bitmap, 29, 14).ShouldBe(((byte)0xc0, (byte)0x60, (byte)0x30, (byte)255));
        At(bitmap, 9, 5).A.ShouldBe((byte)0);
        At(bitmap, 30, 14).A.ShouldBe((byte)0);
        At(bitmap, 10, 15).A.ShouldBe((byte)0);
        Area(bitmap).ShouldBe(200, 1e-6);

        // Half a pixel in x is exact (spans are measured); the colour is not diluted, only the alpha.
        bitmap = Draw("<rect x=\"10.5\" y=\"5\" width=\"1\" height=\"10\" fill=\"#c06030\"/>");
        At(bitmap, 10, 6).ShouldBe(((byte)0xc0, (byte)0x60, (byte)0x30, (byte)128));
        At(bitmap, 11, 6).A.ShouldBe((byte)128);
    }

    [Theory]
    [InlineData("<circle cx=\"20\" cy=\"20\" r=\"15\"/>", Math.PI * 225)]
    [InlineData("<ellipse cx=\"20\" cy=\"20\" rx=\"17\" ry=\"6\"/>", Math.PI * 17 * 6)]
    [InlineData("<path d=\"M5 5 L35 5 L20 35 Z\"/>", 450)]
    [InlineData("<rect x=\"5\" y=\"5\" width=\"30\" height=\"30\" rx=\"10\"/>", 900 - (4 - Math.PI) * 100)]
    // Open subpaths are filled as if closed.
    [InlineData("<path d=\"M5 5 L35 5 L20 35\"/>", 450)]
    // A transform with a rotation and a scale: the area scales by the determinant.
    [InlineData("<rect x=\"-5\" y=\"-5\" width=\"10\" height=\"10\" transform=\"translate(20 20) rotate(30) scale(2 1.5)\"/>", 300)]
    public void AFilledShape_HasItsArea(string shape, double area)
    {
        Area(Draw(shape)).ShouldBe(area, area * 0.004);
    }

    [Fact]
    public void TheFillRule_DecidesTheMiddleOfAStar()
    {
        const string star = "<path d=\"M20 2 L31 36 L2 14 L38 14 L9 36 Z\" fill-rule=\"{0}\"/>";
        At(Draw(string.Format(star, "nonzero")), 20, 20).A.ShouldBe((byte)255);
        At(Draw(string.Format(star, "evenodd")), 20, 20).A.ShouldBe((byte)0);
        // The points of the star are inside under both.
        At(Draw(string.Format(star, "evenodd")), 20, 8).A.ShouldBe((byte)255);
    }

    [Theory]
    // A 20-long line 4 wide: butt is the bare rectangle, square adds half the width at each end, round two half discs.
    [InlineData("butt", 80)]
    [InlineData("square", 96)]
    [InlineData("round", 80 + Math.PI * 4)]
    public void AStrokedLine_HasTheAreaItsCapsGiveIt(string cap, double area)
    {
        Area(Draw($"<path d=\"M10 20 H30\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" stroke-linecap=\"{cap}\"/>")).ShouldBe(area, area * 0.01);
    }

    [Theory]
    // A right angle of two 15-long lines 6 wide. Bevel fills the notch with a triangle, miter with a square, round with a quarter disc.
    [InlineData("bevel", 180 + 4.5)]
    [InlineData("miter", 180 + 9)]
    [InlineData("round", 180 + Math.PI * 9 / 4)]
    // A miter limit below what a right angle needs (1.414) falls back to the bevel.
    [InlineData("miter\" stroke-miterlimit=\"1.2", 180 + 4.5)]
    public void AStrokedCorner_HasTheAreaItsJoinGivesIt(string join, double area)
    {
        // Two 15 x 6 rectangles overlap in a 3 x 3 square at the inside of the corner; the join fills the outside notch.
        Area(Draw($"<path d=\"M10 10 H25 V25\" fill=\"none\" stroke=\"#000\" stroke-width=\"6\" stroke-linejoin=\"{join}\"/>")).ShouldBe(area - 9, area * 0.01);
    }

    [Theory]
    // 40 long, dashes of 5 and gaps of 5: 20 of it is drawn wherever the pattern starts.
    [InlineData("5", "0", 80)]
    [InlineData("5", "5", 80)]
    [InlineData("5", "2.5", 80)]
    [InlineData("10 5", "0", 120)]
    [InlineData("10 5", "-5", 100)]
    // Six whole periods of 6 (24 on) and 4 more, of which 3 on and 1 off: 27 on.
    [InlineData("3 1 1 1", "0", 108)]
    public void ADashedLine_IsCutByItsPattern(string dashes, string offset, double area)
    {
        ColorBitmap bitmap = Draw($"<path d=\"M0 20 H40\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" stroke-dasharray=\"{dashes}\" stroke-dashoffset=\"{offset}\"/>");
        Area(bitmap).ShouldBe(area, 1.0);
    }

    [Fact]
    public void ADashPattern_StartsWhereItsOffsetSays()
    {
        const string line = "<path d=\"M0 20 H40\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" stroke-dasharray=\"10\" stroke-dashoffset=\"{0}\"/>";
        At(Draw(string.Format(line, 0)), 5, 20).A.ShouldBe((byte)255);
        At(Draw(string.Format(line, 0)), 15, 20).A.ShouldBe((byte)0);
        // A positive offset moves the pattern back along the line: the line now starts in a gap.
        At(Draw(string.Format(line, 10)), 5, 20).A.ShouldBe((byte)0);
        At(Draw(string.Format(line, 10)), 15, 20).A.ShouldBe((byte)255);
    }

    [Fact]
    public void AStroke_IsDistortedByATransformThatIsNotUniform()
    {
        // A circle of radius 5 with a stroke of 2, stretched four times as wide: the stroke is 8 wide at the sides and 2 at the top.
        ColorBitmap bitmap = Draw("<circle r=\"5\" fill=\"none\" stroke=\"#000\" stroke-width=\"2\" transform=\"translate(40 20) scale(4 1)\"/>", 80, 40);
        int Run(Func<int, (int X, int Y)> at, int count) => Enumerable.Range(0, count).Count(i => At(bitmap, at(i).X, at(i).Y).A > 127);
        Run(i => (i, 20), 40).ShouldBe(8);     // across the left side, along the middle row
        Run(i => (40, i), 20).ShouldBe(2);     // down through the top
    }

    [Fact]
    public void AGroup_FadesAsOneLayer_SoAnOverlapIsNotDarker()
    {
        const string squares = "<rect x=\"5\" y=\"5\" width=\"20\" height=\"20\" fill=\"#f00\"/><rect x=\"15\" y=\"15\" width=\"20\" height=\"20\" fill=\"#f00\"/>";
        ColorBitmap grouped = Draw($"<g opacity=\"0.5\">{squares}</g>");
        At(grouped, 10, 10).A.ShouldBe((byte)128);
        At(grouped, 20, 20).A.ShouldBe((byte)128);     // the overlap: once, not twice

        // Without the group each fades on its own, and the overlap is covered twice.
        ColorBitmap separate = Draw(squares.Replace("fill=", "opacity=\"0.5\" fill="));
        At(separate, 10, 10).A.ShouldBe((byte)128);
        At(separate, 20, 20).A.ShouldBe((byte)191);
    }

    [Fact]
    public void LaterShapesCoverEarlierOnes_AndFillIsUnderStroke()
    {
        ColorBitmap bitmap = Draw("<rect width=\"40\" height=\"40\" fill=\"#00f\"/><rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#0f0\" stroke=\"#f00\" stroke-width=\"4\"/>");
        At(bitmap, 2, 2).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));
        At(bitmap, 20, 20).ShouldBe(((byte)0, (byte)255, (byte)0, (byte)255));
        At(bitmap, 10, 20).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));   // the stroke straddles the edge, over the fill
        At(bitmap, 9, 20).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));
    }

    [Fact]
    public void ALinearGradient_RunsFromItsFirstStopToItsLast_AcrossTheShape()
    {
        ColorBitmap bitmap = Draw(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#000\"/><stop offset=\"1\" stop-color=\"#fff\"/></linearGradient></defs>" +
            "<rect width=\"40\" height=\"40\" fill=\"url(#g)\"/>");
        // The colour at a pixel is taken at its centre: (x + 0.5) / 40 of the way.
        At(bitmap, 0, 20).R.ShouldBe((byte)3);
        At(bitmap, 19, 3).R.ShouldBe((byte)124);
        At(bitmap, 39, 20).R.ShouldBe((byte)252);
        At(bitmap, 19, 3).A.ShouldBe((byte)255);
    }

    [Fact]
    public void ARadialGradient_RunsFromItsCentreToItsEdge_AndPadsBeyond()
    {
        ColorBitmap bitmap = Draw(
            "<defs><radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" cx=\"20\" cy=\"20\" r=\"10\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></radialGradient></defs>" +
            "<rect width=\"40\" height=\"40\" fill=\"url(#g)\"/>");
        At(bitmap, 20, 20).R.ShouldBeGreaterThan((byte)235);
        At(bitmap, 25, 20).R.ShouldBeInRange((byte)110, (byte)135);     // about half way out
        At(bitmap, 35, 20).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));
        At(bitmap, 2, 2).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));
    }

    [Fact]
    public void AStopThatFadesOut_BlendsColourAndAlphaStraight()
    {
        ColorBitmap bitmap = Draw(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\" stop-opacity=\"0\"/></linearGradient></defs>" +
            "<rect width=\"40\" height=\"40\" fill=\"url(#g)\"/>");
        // Half way: half red and half blue, at half alpha. (Blended premultiplied it would still be pure red.)
        (byte r, _, byte b, byte a) = At(bitmap, 19, 20);
        ((int)r).ShouldBeInRange(128, 136);
        ((int)b).ShouldBeInRange(119, 127);
        ((int)a).ShouldBeInRange(128, 136);
    }

    [Fact]
    public void TheSceneIsScaledToTheSizeAskedFor_AndCoverageIsItsAlpha()
    {
        VectorScene scene = Scene("<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#123456\" fill-opacity=\"0.5\"/>");
        ColorBitmap big = SceneRasterizer.Rasterize(scene, 80, 80);
        At(big, 20, 20).ShouldBe(((byte)0x12, (byte)0x34, (byte)0x56, (byte)128));
        At(big, 59, 59).A.ShouldBe((byte)128);
        At(big, 19, 20).A.ShouldBe((byte)0);
        Area(big).ShouldBe(1600 * 128 / 255.0, 1e-6);

        byte[] coverage = SceneRasterizer.RasterizeCoverage(scene, 80, 80);
        coverage.Length.ShouldBe(6400);
        for (int i = 0; i < coverage.Length; i++) coverage[i].ShouldBe(big.Pixels[i * 4 + 3]);

        // An explicit transform instead: the scene's own units, moved.
        ColorBitmap moved = SceneRasterizer.Rasterize(scene, 40, 40, Matrix3x2.CreateTranslation(-10, -10));
        At(moved, 0, 0).A.ShouldBe((byte)128);
        At(moved, 20, 20).A.ShouldBe((byte)0);
    }

    [Fact]
    public void WhatCannotBeDrawn_IsRefusedOrIsNothing()
    {
        VectorScene scene = Scene("<rect width=\"10\" height=\"10\"/>");
        Should.Throw<ArgumentOutOfRangeException>(() => SceneRasterizer.Rasterize(scene, 0, 10));
        Should.Throw<ArgumentOutOfRangeException>(() => SceneRasterizer.Rasterize(scene, 10, -1));
        Should.Throw<ArgumentOutOfRangeException>(() => SceneRasterizer.Rasterize(scene, 1 << 14, 1 << 14));
        Should.Throw<ArgumentNullException>(() => SceneRasterizer.Rasterize(null!, 10, 10));

        // Entirely off the canvas, or squashed flat: an empty picture, not an error.
        Area(SceneRasterizer.Rasterize(scene, 10, 10, Matrix3x2.CreateTranslation(100, 100))).ShouldBe(0);
        Area(SceneRasterizer.Rasterize(scene, 10, 10, Matrix3x2.CreateScale(1, 0))).ShouldBe(0);
        Area(Draw("")).ShouldBe(0);
    }

    [Fact]
    public void AnAbsurdlyFineDashPattern_IsDrawnSolid_NotCutIntoMillions()
    {
        // 40 px of line and dashes a ten-thousandth of a pixel long.
        ColorBitmap bitmap = Draw("<path d=\"M0 20 H40\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" stroke-dasharray=\"0.0001\"/>");
        Area(bitmap).ShouldBe(160, 1.0);
    }
}
