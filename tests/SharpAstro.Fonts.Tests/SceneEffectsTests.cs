using System.Numerics;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Fonts.Tables.Colr;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Blend modes and filters against things that can be worked out by hand: the specification's formulas at chosen
/// colours, and pictures whose pixels are known. <see cref="SceneRasterizerBrowserTests"/> holds the same code
/// against a browser, which is what says the formulas were read rightly; these run with no browser.
/// </summary>
public sealed class SceneEffectsTests
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
    private static double Ink(ColorBitmap bitmap)
    {
        double sum = 0;
        for (int i = 3; i < bitmap.Pixels.Length; i += 4) sum += bitmap.Pixels[i];
        return sum / 255.0;
    }

    private static void ShouldBeNear((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int a, int within = 1, string? what = null)
    {
        ((int)actual.R).ShouldBeInRange(r - within, r + within, what);
        ((int)actual.G).ShouldBeInRange(g - within, g + within, what);
        ((int)actual.B).ShouldBeInRange(b - within, b + within, what);
        ((int)actual.A).ShouldBeInRange(a - within, a + within, what);
    }

    // ---- The compositing arithmetic ----------------------------------------------------------------------------------

    private static readonly Vector3 Below = new(0.2f, 0.5f, 0.8f), Above = new(0.6f, 0.4f, 0.1f);

    // The mixed colour for a backdrop of (0.2, 0.5, 0.8) and a source of (0.6, 0.4, 0.1), worked out apart from the code
    // from the formulas of the W3C Compositing and Blending specification.
    [Theory]
    [InlineData(VectorBlendMode.Normal, 0.6f, 0.4f, 0.1f)]
    [InlineData(VectorBlendMode.Multiply, 0.12000f, 0.20000f, 0.08000f)]
    [InlineData(VectorBlendMode.Screen, 0.68000f, 0.70000f, 0.82000f)]
    [InlineData(VectorBlendMode.Overlay, 0.24000f, 0.40000f, 0.64000f)]
    [InlineData(VectorBlendMode.Darken, 0.20000f, 0.40000f, 0.10000f)]
    [InlineData(VectorBlendMode.Lighten, 0.60000f, 0.50000f, 0.80000f)]
    [InlineData(VectorBlendMode.ColorDodge, 0.50000f, 0.83333f, 0.88889f)]
    [InlineData(VectorBlendMode.ColorBurn, 0.00000f, 0.00000f, 0.00000f)]
    [InlineData(VectorBlendMode.HardLight, 0.36000f, 0.40000f, 0.16000f)]
    [InlineData(VectorBlendMode.SoftLight, 0.24960f, 0.45000f, 0.67200f)]
    [InlineData(VectorBlendMode.Difference, 0.40000f, 0.10000f, 0.70000f)]
    [InlineData(VectorBlendMode.Exclusion, 0.56000f, 0.50000f, 0.74000f)]
    [InlineData(VectorBlendMode.Hue, 0.65060f, 0.41060f, 0.05060f)]
    [InlineData(VectorBlendMode.Saturation, 0.24050f, 0.49050f, 0.74050f)]
    [InlineData(VectorBlendMode.Color, 0.61600f, 0.41600f, 0.11600f)]
    [InlineData(VectorBlendMode.Luminosity, 0.18400f, 0.48400f, 0.78400f)]
    public void ABlendMode_MixesTwoOpaqueColours_AsTheSpecificationSays(VectorBlendMode mode, float r, float g, float b)
    {
        Vector4 mixed = Compositing.Blend(mode, new Vector4(Below, 1), new Vector4(Above, 1));
        mixed.X.ShouldBe(r, 1e-4f);
        mixed.Y.ShouldBe(g, 1e-4f);
        mixed.Z.ShouldBe(b, 1e-4f);
        mixed.W.ShouldBe(1f);
    }

    [Fact]
    public void TheModesWithACliff_HaveItWhereTheSpecificationPutsIt()
    {
        static float Red(VectorBlendMode mode, float below, float above) =>
            Compositing.Blend(mode, new Vector4(below, 0, 0, 1), new Vector4(above, 0, 0, 1)).X;

        // Burn: 1 - (1 - Cb) / Cs, but white stays white and a black source gives black.
        Red(VectorBlendMode.ColorBurn, 0.9f, 0.5f).ShouldBe(0.8f, 1e-5f);
        Red(VectorBlendMode.ColorBurn, 1f, 0f).ShouldBe(1f);
        Red(VectorBlendMode.ColorBurn, 0.5f, 0f).ShouldBe(0f);
        // Dodge: Cb / (1 - Cs), but black stays black and a white source gives white.
        Red(VectorBlendMode.ColorDodge, 0f, 1f).ShouldBe(0f);
        Red(VectorBlendMode.ColorDodge, 0.5f, 1f).ShouldBe(1f);
        // Soft light changes its curve at a quarter of the backdrop, and overlay is hard light the other way round.
        Red(VectorBlendMode.SoftLight, 0.1f, 0.9f).ShouldBe(0.1f + 0.8f * (((16 * 0.1f - 12) * 0.1f + 4) * 0.1f - 0.1f), 1e-5f);
        Red(VectorBlendMode.Overlay, 0.7f, 0.2f).ShouldBe(Red(VectorBlendMode.HardLight, 0.2f, 0.7f), 1e-6f);
        MathF.Abs(Red(VectorBlendMode.Overlay, 0.7f, 0.2f) - Red(VectorBlendMode.HardLight, 0.7f, 0.2f)).ShouldBeGreaterThan(0.1f);
    }

    [Fact]
    public void WhereOnlyOneOfTheTwoIsThere_ThatOneIsWhatComesOut()
    {
        var source = new Vector4(Above * 0.5f, 0.5f);       // half there, premultiplied
        var backdrop = new Vector4(Below * 0.25f, 0.25f);   // a quarter there
        foreach (VectorBlendMode mode in Enum.GetValues<VectorBlendMode>())
        {
            Compositing.Blend(mode, default, source).ShouldBe(source, mode.ToString());
            Compositing.Blend(mode, backdrop, default).ShouldBe(backdrop, mode.ToString());
        }

        // Both partly there: the source alone where the backdrop is not, the mix where both are, the backdrop alone
        // where the source is not; an eighth of the pixel has both.
        Vector4 multiplied = Compositing.Blend(VectorBlendMode.Multiply, backdrop, source);
        multiplied.W.ShouldBe(0.5f + 0.25f - 0.125f, 1e-6f);
        Vector3 expected = Above * (0.5f - 0.125f) + Above * Below * 0.125f + Below * (0.25f - 0.125f);
        new Vector3(multiplied.X, multiplied.Y, multiplied.Z).ShouldBe(expected);
    }

    [Fact]
    public void ColrsCompositeModes_ArePorterAndDuffs_AndTheBlendModesByName()
    {
        var source = new Vector4(Above * 0.5f, 0.5f);
        var backdrop = new Vector4(Below * 0.25f, 0.25f);
        Compositing.Composite(CompositeMode.Clear, backdrop, source).ShouldBe(default);
        Compositing.Composite(CompositeMode.Src, backdrop, source).ShouldBe(source);
        Compositing.Composite(CompositeMode.Dest, backdrop, source).ShouldBe(backdrop);
        Compositing.Composite(CompositeMode.SrcOver, backdrop, source).ShouldBe(source + backdrop * 0.5f);
        Compositing.Composite(CompositeMode.DestOver, backdrop, source).ShouldBe(backdrop + source * 0.75f);
        Compositing.Composite(CompositeMode.SrcIn, backdrop, source).ShouldBe(source * 0.25f);
        Compositing.Composite(CompositeMode.DestIn, backdrop, source).ShouldBe(backdrop * 0.5f);
        Compositing.Composite(CompositeMode.SrcOut, backdrop, source).ShouldBe(source * 0.75f);
        Compositing.Composite(CompositeMode.DestOut, backdrop, source).ShouldBe(backdrop * 0.5f);
        Compositing.Composite(CompositeMode.SrcAtop, backdrop, source).ShouldBe(source * 0.25f + backdrop * 0.5f);
        Compositing.Composite(CompositeMode.DestAtop, backdrop, source).ShouldBe(backdrop * 0.5f + source * 0.75f);
        Compositing.Composite(CompositeMode.Xor, backdrop, source).ShouldBe(source * 0.75f + backdrop * 0.5f);
        Vector4.Distance(Compositing.Composite(CompositeMode.Plus, new Vector4(0.6f, 0.2f, 0, 0.7f), new Vector4(0.6f, 0.1f, 0, 0.6f)), new Vector4(1, 0.3f, 0, 1)).ShouldBeLessThan(1e-6f);

        (CompositeMode, VectorBlendMode)[] named =
        [
            (CompositeMode.Screen, VectorBlendMode.Screen), (CompositeMode.Overlay, VectorBlendMode.Overlay), (CompositeMode.Darken, VectorBlendMode.Darken),
            (CompositeMode.Lighten, VectorBlendMode.Lighten), (CompositeMode.ColorDodge, VectorBlendMode.ColorDodge), (CompositeMode.ColorBurn, VectorBlendMode.ColorBurn),
            (CompositeMode.HardLight, VectorBlendMode.HardLight), (CompositeMode.SoftLight, VectorBlendMode.SoftLight), (CompositeMode.Difference, VectorBlendMode.Difference),
            (CompositeMode.Exclusion, VectorBlendMode.Exclusion), (CompositeMode.Multiply, VectorBlendMode.Multiply), (CompositeMode.HslHue, VectorBlendMode.Hue),
            (CompositeMode.HslSaturation, VectorBlendMode.Saturation), (CompositeMode.HslColor, VectorBlendMode.Color), (CompositeMode.HslLuminosity, VectorBlendMode.Luminosity),
        ];
        var opaqueBelow = new Vector4(Below, 1);
        var opaqueAbove = new Vector4(Above, 1);
        foreach ((CompositeMode colr, VectorBlendMode mode) in named)
        {
            Compositing.Composite(colr, opaqueBelow, opaqueAbove).ShouldBe(Compositing.Blend(mode, opaqueBelow, opaqueAbove), colr.ToString());
            // And none of them is plain source-over, which is what the four HSL modes were until 2.0.
            Compositing.Composite(colr, opaqueBelow, opaqueAbove).ShouldNotBe(opaqueAbove, colr.ToString());
        }
    }

    [Fact]
    public void AColourThatLeavesTheRange_IsBroughtBackTowardsItsGrey()
    {
        static Vector4 Coloured(Vector3 below, Vector3 above) => Compositing.Blend(VectorBlendMode.Color, new Vector4(below, 1), new Vector4(above, 1));

        // Red at the luminosity of a near white: (1.689, 0.689, 0.689) before it is brought back, so the top is cut to 1.
        Vector4 light = Coloured(new Vector3(1, 1, 0.9f), new Vector3(1, 0, 0));
        Vector4.Distance(light, new Vector4(1, 0.98429f, 0.98429f, 1)).ShouldBeLessThan(1e-4f);
        // Red at the luminosity of a near black: (0.711, -0.289, -0.289) before, so the bottom is cut to 0.
        Vector4 dark = Coloured(new Vector3(0, 0, 0.1f), new Vector3(1, 0, 0));
        Vector4.Distance(dark, new Vector4(0.036667f, 0, 0, 1)).ShouldBeLessThan(1e-4f);
    }

    [Fact]
    public void AColrCompositeOfBytes_KeepsAMidColourAMidColour()
    {
        // The surfaces of a COLR glyph hold bytes that are not premultiplied. Until 2.0 these were put on a scale of 0 to
        // 255 against alphas of 0 to 1, and every channel that was not zero came out as 255.
        byte[] grey = [128, 64, 200, 255], nothing = [0, 0, 0, 0], half = [200, 100, 50, 128], white = [255, 255, 255, 255];
        ColrRenderer.CompositePixel(CompositeMode.SrcOver, nothing, grey).ShouldBe(((byte)128, (byte)64, (byte)200, (byte)255));
        ColrRenderer.CompositePixel(CompositeMode.SrcOver, grey, nothing).ShouldBe(((byte)128, (byte)64, (byte)200, (byte)255));
        ColrRenderer.CompositePixel(CompositeMode.Src, white, half).ShouldBe(((byte)200, (byte)100, (byte)50, (byte)128));
        ColrRenderer.CompositePixel(CompositeMode.SrcIn, half, grey).ShouldBe(((byte)128, (byte)64, (byte)200, (byte)128));
        ColrRenderer.CompositePixel(CompositeMode.Clear, grey, grey).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)0));
        // Multiply of two opaque colours is their product, channel by channel: 128/255 * 128/255, 64/255 * 64/255, 200/255 * 200/255.
        ColrRenderer.CompositePixel(CompositeMode.Multiply, grey, grey).ShouldBe(((byte)64, (byte)16, (byte)157, (byte)255));
        // Half a colour over white: half of each.
        ColrRenderer.CompositePixel(CompositeMode.SrcOver, white, half).ShouldBe(((byte)227, (byte)177, (byte)152, (byte)255));
    }

    // ---- Blend modes in a scene --------------------------------------------------------------------------------------

    [Fact]
    public void ABlendedShape_IsMixedWithWhatIsBelowIt_AndIsItselfWhereNothingIs()
    {
        ColorBitmap bitmap = Draw("<rect width=\"20\" height=\"40\" fill=\"#3380cc\"/><rect x=\"10\" width=\"20\" height=\"40\" fill=\"#996619\" style=\"mix-blend-mode: multiply\"/>");
        At(bitmap, 5, 20).ShouldBe(((byte)0x33, (byte)0x80, (byte)0xcc, (byte)255));
        // 0.2 * 0.6, 0.502 * 0.4, 0.8 * 0.098, of 255.
        ShouldBeNear(At(bitmap, 15, 20), 31, 51, 20, 255);
        // Over nothing there is nothing to multiply by.
        At(bitmap, 25, 20).ShouldBe(((byte)0x99, (byte)0x66, (byte)0x19, (byte)255));
    }

    [Fact]
    public void AGroupAroundABlend_KeepsItFromWhatIsBelowTheGroup()
    {
        const string Ground = "<rect width=\"40\" height=\"40\" fill=\"#808080\"/>";
        const string Pair = "<rect x=\"5\" width=\"15\" height=\"40\" fill=\"#ffffff\"/><rect x=\"10\" width=\"20\" height=\"40\" fill=\"#ff0000\" style=\"mix-blend-mode: multiply\"/>";

        // Open: the red multiplies with the white (red) and, beside it, with the grey ground (dark red).
        ColorBitmap open = Draw($"{Ground}<g>{Pair}</g>");
        ShouldBeNear(At(open, 15, 20), 255, 0, 0, 255);
        ShouldBeNear(At(open, 25, 20), 128, 0, 0, 255);

        // Isolated: with the white only; beside it there is nothing in the group to multiply with, and the red is laid on as it is.
        ColorBitmap closed = Draw($"{Ground}<g style=\"isolation: isolate\">{Pair}</g>");
        ShouldBeNear(At(closed, 15, 20), 255, 0, 0, 255);
        ShouldBeNear(At(closed, 25, 20), 255, 0, 0, 255);
    }

    [Fact]
    public void TheElementsOpacity_IsAppliedBeforeItIsMixed()
    {
        ColorBitmap bitmap = Draw("<rect width=\"40\" height=\"40\" fill=\"#ffffff\"/><rect width=\"40\" height=\"40\" fill=\"#000000\" opacity=\"0.5\" style=\"mix-blend-mode: difference\"/>");
        // Half of |white - black| = white, and half of the white left as it was.
        ShouldBeNear(At(bitmap, 20, 20), 255, 255, 255, 255);
        bitmap = Draw("<rect width=\"40\" height=\"40\" fill=\"#ffffff\"/><rect width=\"40\" height=\"40\" fill=\"#ffffff\" opacity=\"0.5\" style=\"mix-blend-mode: difference\"/>");
        // Half of |white - white| = black, half white.
        ShouldBeNear(At(bitmap, 20, 20), 128, 128, 128, 255);
    }

    // ---- Filters: where things land ----------------------------------------------------------------------------------

    private const string Wide = "x=\"-2\" y=\"-2\" width=\"5\" height=\"5\"";

    [Fact]
    public void AnOffset_MovesTheLayerByWholePixels_Exactly()
    {
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feOffset dx=\"7\" dy=\"-3\"/></filter><rect x=\"10\" y=\"10\" width=\"6\" height=\"4\" fill=\"#204060\" filter=\"url(#f)\"/>");
        Ink(bitmap).ShouldBe(24, 1e-9);
        At(bitmap, 17, 7).ShouldBe(((byte)0x20, (byte)0x40, (byte)0x60, (byte)255));
        At(bitmap, 22, 10).ShouldBe(((byte)0x20, (byte)0x40, (byte)0x60, (byte)255));
        At(bitmap, 16, 7).A.ShouldBe((byte)0);
        At(bitmap, 23, 10).A.ShouldBe((byte)0);
        At(bitmap, 17, 11).A.ShouldBe((byte)0);
        At(bitmap, 12, 12).A.ShouldBe((byte)0);
    }

    [Fact]
    public void TheDistances_AreInTheElementsUnits_SoAScaleScalesThem()
    {
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feOffset dx=\"3\"/></filter><g transform=\"scale(2)\"><rect x=\"2\" y=\"2\" width=\"3\" height=\"3\" fill=\"#000\" filter=\"url(#f)\"/></g>");
        Ink(bitmap).ShouldBe(36, 1e-9);
        At(bitmap, 9, 5).A.ShouldBe((byte)0);
        At(bitmap, 10, 5).A.ShouldBe((byte)255);
        At(bitmap, 15, 9).A.ShouldBe((byte)255);
        At(bitmap, 16, 9).A.ShouldBe((byte)0);

        // The same when it is the picture that is drawn larger and not the element.
        VectorScene scene = Scene($"<filter id=\"f\" {Wide}><feOffset dx=\"3\"/></filter><rect x=\"2\" y=\"2\" width=\"3\" height=\"3\" fill=\"#000\" filter=\"url(#f)\"/>", 20, 20);
        ColorBitmap doubled = SceneRasterizer.Rasterize(scene, 40, 40);
        doubled.Pixels.ShouldBe(bitmap.Pixels);

        // And a blur's deviation: a blur of 1.5 drawn at twice the size is the blur of 3 of the drawing made twice as large.
        VectorScene small = Scene($"<filter id=\"f\" {Wide}><feGaussianBlur stdDeviation=\"1.5\"/></filter><rect x=\"7\" y=\"7\" width=\"6\" height=\"6\" fill=\"#000\" filter=\"url(#f)\"/>", 20, 20);
        ColorBitmap large = Draw($"<filter id=\"f\" {Wide}><feGaussianBlur stdDeviation=\"3\"/></filter><rect x=\"14\" y=\"14\" width=\"12\" height=\"12\" fill=\"#000\" filter=\"url(#f)\"/>");
        SceneRasterizer.Rasterize(small, 40, 40).Pixels.ShouldBe(large.Pixels);
    }

    [Fact]
    public void NothingIsDrawnOutsideTheFiltersArea_ButTheLayerOutsideItStillCounts()
    {
        // The area is the square 10..20; the layer is a bar across the whole picture.
        const string Filter = "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10\" y=\"10\" width=\"10\" height=\"10\">STEP</filter><rect y=\"12\" width=\"40\" height=\"6\" fill=\"#000\" filter=\"url(#f)\"/>";
        ColorBitmap moved = Draw(Filter.Replace("STEP", "<feOffset dx=\"-25\"/>"));
        // What ends up in the area was at x = 35..45, outside it: it is there all the same, up to where the bar ends at 40.
        Ink(moved).ShouldBe(5 * 6, 1e-9);
        At(moved, 10, 14).A.ShouldBe((byte)255);
        At(moved, 14, 14).A.ShouldBe((byte)255);
        At(moved, 15, 14).A.ShouldBe((byte)0);
        At(moved, 9, 14).A.ShouldBe((byte)0);

        // A blur along the bar leaves it as solid inside the area as it was: nothing fades at the area's edge.
        ColorBitmap blurred = Draw(Filter.Replace("STEP", "<feGaussianBlur stdDeviation=\"3 0\"/>"));
        Ink(blurred).ShouldBe(10 * 6, 0.05);
        At(blurred, 10, 14).A.ShouldBe((byte)255);
        At(blurred, 19, 14).A.ShouldBe((byte)255);
        At(blurred, 20, 14).A.ShouldBe((byte)0);
    }

    [Fact]
    public void WhatIsOffThePicture_CanBeBroughtOntoIt()
    {
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"-100\" y=\"0\" width=\"200\" height=\"40\"><feOffset dx=\"30\"/></filter>" +
            "<rect x=\"-25\" y=\"10\" width=\"10\" height=\"10\" fill=\"#c00\" filter=\"url(#f)\"/>");
        Ink(bitmap).ShouldBe(100, 1e-9);
        At(bitmap, 5, 10).ShouldBe(((byte)0xcc, (byte)0, (byte)0, (byte)255));
        At(bitmap, 14, 19).A.ShouldBe((byte)255);
        At(bitmap, 15, 19).A.ShouldBe((byte)0);
    }

    // ---- Filters: the blur -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.5")]     // the Gaussian itself
    [InlineData("1")]       // three boxes, an even one
    [InlineData("1.5")]     // an odd one
    [InlineData("3")]
    [InlineData("2 0.7")]
    public void ABlur_LosesNoInk_AndIsTheSameOnBothSides(string deviation)
    {
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feGaussianBlur stdDeviation=\"{deviation}\"/></filter><rect x=\"16\" y=\"16\" width=\"8\" height=\"8\" fill=\"#000\" filter=\"url(#f)\"/>");
        Ink(bitmap).ShouldBe(64, 0.6);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                ((int)At(bitmap, x, y).A).ShouldBeInRange(At(bitmap, 39 - x, y).A - 1, At(bitmap, 39 - x, y).A + 1, $"{x},{y} against its mirror");
                ((int)At(bitmap, x, y).A).ShouldBeInRange(At(bitmap, x, 39 - y).A - 1, At(bitmap, x, 39 - y).A + 1, $"{x},{y} against its mirror");
            }
        }
        // Blurred at all: the corner is no longer solid, and there is ink outside the square.
        ((int)At(bitmap, 16, 16).A).ShouldBeLessThan(250);
        ((int)At(bitmap, 15, 20).A).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ABlurOfOne_IsThreeBoxes_AsABrowsersIs()
    {
        // An edge at x = 20 blurred by 1 across: boxes of 2, 2 and 3 pixels, which spread the edge as 1, 3, 4, 3, 1
        // twelfths over five pixels. (The Gaussian would give 0.6, 6.1, 24.2, 38.3, ... per cent: not these.)
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\"><feGaussianBlur stdDeviation=\"1 0\"/></filter>" +
            "<rect x=\"20\" width=\"20\" height=\"40\" fill=\"#000\" filter=\"url(#f)\"/>");
        int[] alpha = [.. Enumerable.Range(17, 6).Select(x => (int)At(bitmap, x, 20).A)];
        int[] expected = [0, 21, 85, 170, 234, 255];
        for (int i = 0; i < expected.Length; i++) alpha[i].ShouldBeInRange(expected[i] - 1, expected[i] + 1, $"pixel {17 + i}: {string.Join(" ", alpha)}");
    }

    [Fact]
    public void ABlur_MixesColoursInLinearLight_UnlessToldToUseTheValuesAsTheyAre()
    {
        const string Body = "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" SPACE><feGaussianBlur stdDeviation=\"4 0\"/></filter>" +
            "<g filter=\"url(#f)\"><rect width=\"20\" height=\"40\" fill=\"#ff0000\"/><rect x=\"20\" width=\"20\" height=\"40\" fill=\"#00ff00\"/></g>";
        // Halfway between the two, half of each. In linear light a half is 188 of 255; on the values as they are it is 128.
        ColorBitmap linear = Draw(Body.Replace("SPACE", ""));
        ((int)At(linear, 19, 20).R + At(linear, 20, 20).R).ShouldBeInRange(2 * 188 - 4, 2 * 188 + 4);
        ((int)At(linear, 19, 20).G + At(linear, 20, 20).G).ShouldBeInRange(2 * 188 - 4, 2 * 188 + 4);
        ColorBitmap plain = Draw(Body.Replace("SPACE", "color-interpolation-filters=\"sRGB\""));
        ((int)At(plain, 19, 20).R + At(plain, 20, 20).R).ShouldBeInRange(2 * 128 - 3, 2 * 128 + 3);
        ((int)At(plain, 19, 20).G + At(plain, 20, 20).G).ShouldBeInRange(2 * 128 - 3, 2 * 128 + 3);
    }

    [Fact]
    public void AnAbsurdBlur_IsWorkedOutCoarser_NotOnAPictureOfItsOwnSize()
    {
        // A deviation of a hundred thousand pixels reaches three hundred thousand each way: a buffer of that size would be
        // 10^11 pixels. It is drawn, quickly, and what little of the square is left over the picture is next to nothing.
        long before = GC.GetAllocatedBytesForCurrentThread();
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"-1e6\" y=\"-1e6\" width=\"2e6\" height=\"2e6\"><feGaussianBlur stdDeviation=\"100000\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#000\" filter=\"url(#f)\"/>");
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(3L << 30);
        Ink(bitmap).ShouldBeLessThan(1);
    }

    // ---- Filters: the other steps ------------------------------------------------------------------------------------

    [Fact]
    public void AFlood_FillsItsOwnArea_AndAMergeLaysPicturesOverEachOther()
    {
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"#0000ff\" x=\"5\" y=\"5\" width=\"20\" height=\"10\" result=\"blue\" color-interpolation-filters=\"sRGB\"/>" +
            "<feFlood flood-color=\"#ff0000\" flood-opacity=\"0.5\" x=\"15\" y=\"0\" width=\"10\" height=\"40\" result=\"red\" color-interpolation-filters=\"sRGB\"/>" +
            "<feMerge color-interpolation-filters=\"sRGB\"><feMergeNode in=\"blue\"/><feMergeNode in=\"SourceGraphic\"/><feMergeNode in=\"red\"/></feMerge></filter>" +
            "<rect x=\"20\" y=\"8\" width=\"10\" height=\"4\" fill=\"#00ff00\" filter=\"url(#f)\"/>");
        At(bitmap, 10, 10).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255));         // the blue alone
        At(bitmap, 10, 20).A.ShouldBe((byte)0);                                        // below it: nothing
        ShouldBeNear(At(bitmap, 17, 10), 128, 0, 128, 255);                            // half red over blue
        ShouldBeNear(At(bitmap, 22, 10), 128, 128, 0, 255);                            // half red over the green square over blue
        At(bitmap, 27, 10).ShouldBe(((byte)0, (byte)255, (byte)0, (byte)255));         // the square alone
        ShouldBeNear(At(bitmap, 17, 30), 255, 0, 0, 128);                              // half red over nothing
    }

    [Fact]
    public void APicture_IsChangedOver_WhenAStepOfTheOtherKindReadsIt()
    {
        // A flood made on the values as they are, read by a step that works in linear light, and shown: the colour it was.
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"#804020\" color-interpolation-filters=\"sRGB\"/><feOffset dx=\"1\"/></filter><rect width=\"4\" height=\"4\" filter=\"url(#f)\"/>");
        ShouldBeNear(At(bitmap, 20, 20), 0x80, 0x40, 0x20, 255);
        // And the other way round.
        bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            "<feFlood flood-color=\"#804020\"/><feOffset dx=\"1\" color-interpolation-filters=\"sRGB\"/></filter><rect width=\"4\" height=\"4\" filter=\"url(#f)\"/>");
        ShouldBeNear(At(bitmap, 20, 20), 0x80, 0x40, 0x20, 255);
    }

    [Fact]
    public void RecolouringWorksOnTheColour_NotOnTheColourTimesItsAlpha()
    {
        const string Filter = "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" color-interpolation-filters=\"sRGB\">STEP</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#336699\" fill-opacity=\"0.5\" filter=\"url(#f)\"/>";
        // Half there: the channels are moved round as they are, and it stays half there.
        ShouldBeNear(At(Draw(Filter.Replace("STEP", "<feColorMatrix values=\"0 1 0 0 0  0 0 1 0 0  1 0 0 0 0  0 0 0 1 0\"/>")), 20, 20), 0x66, 0x99, 0x33, 128, within: 2);
        ShouldBeNear(At(Draw(Filter.Replace("STEP", "<feComponentTransfer><feFuncR type=\"linear\" slope=\"2\"/></feComponentTransfer>")), 20, 20), 0x66, 0x66, 0x99, 128, within: 2);
    }

    [Theory]
    [InlineData("in", 255, 0, 0, 0)]                // the bar where the flood is; nothing where only one of the two is
    [InlineData("out", 0, 0, 0, 255)]
    [InlineData("atop", 255, 255, 255, 0)]          // on top of the flood where it is, and the flood kept beside it
    [InlineData("xor", 0, 255, 255, 255)]
    [InlineData("over", 255, 255, 255, 255)]
    public void AComposite_PutsTheLayerOnAFlood(string op, int both, int floodOnly, int floodOnlyAgain, int layerOnly)
    {
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\">" +
            $"<feFlood flood-color=\"#00f\" x=\"0\" y=\"0\" width=\"20\" height=\"40\" result=\"half\"/><feComposite in=\"SourceGraphic\" in2=\"half\" operator=\"{op}\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"10\" fill=\"#f00\" filter=\"url(#f)\"/>");
        ((int)At(bitmap, 15, 15).A).ShouldBe(both);
        ((int)At(bitmap, 5, 15).A).ShouldBe(floodOnly);
        ((int)At(bitmap, 15, 30).A).ShouldBe(floodOnlyAgain);
        ((int)At(bitmap, 25, 15).A).ShouldBe(layerOnly);
        // Where both are and something is kept, "in", "atop" and "over" show the layer and not the flood.
        if (both > 0) At(bitmap, 15, 15).R.ShouldBe((byte)255);
    }

    [Fact]
    public void AnArithmeticComposite_IsItsFourNumbers()
    {
        // 0.5 * in + 0.25 on every premultiplied channel: an opaque mid red (128, 0, 0) gives (0.5 * 0.502 + 0.25, 0.25, 0.25) at an alpha of 0.75,
        // and where there is no layer, 0.25 of everything.
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" color-interpolation-filters=\"sRGB\">" +
            "<feComposite in=\"SourceGraphic\" in2=\"SourceGraphic\" operator=\"arithmetic\" k2=\"0.5\" k4=\"0.25\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#800000\" filter=\"url(#f)\"/>");
        ShouldBeNear(At(bitmap, 20, 20), (int)MathF.Round((0.5f * 128 / 255 + 0.25f) / 0.75f * 255), 85, 85, 191);
        ShouldBeNear(At(bitmap, 5, 5), 255, 255, 255, 64);
    }

    [Fact]
    public void AColourMatrix_AndAComponentTransfer_RecolourEveryPixel()
    {
        const string Filter = "<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"40\" height=\"40\" color-interpolation-filters=\"sRGB\">STEP</filter>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#336699\" filter=\"url(#f)\"/>";
        // Channels moved round, and the alpha to four fifths.
        ColorBitmap swapped = Draw(Filter.Replace("STEP", "<feColorMatrix values=\"0 1 0 0 0  0 0 1 0 0  1 0 0 0 0  0 0 0 0.8 0\"/>"));
        ShouldBeNear(At(swapped, 20, 20), 0x66, 0x99, 0x33, 204);
        At(swapped, 5, 5).A.ShouldBe((byte)0);
        // No saturation: the three weights of the one grey.
        ColorBitmap grey = Draw(Filter.Replace("STEP", "<feColorMatrix type=\"saturate\" values=\"0\"/>"));
        int level = (int)MathF.Round(0.213f * 0x33 + 0.715f * 0x66 + 0.072f * 0x99);
        ShouldBeNear(At(grey, 20, 20), level, level, level, 255);
        // The matrix's fifth column gives colour to what had none.
        ColorBitmap filled = Draw(Filter.Replace("STEP", "<feColorMatrix values=\"0 0 0 0 1  0 0 0 0 0  0 0 0 0 0  0 0 0 0 0.5\"/>"));
        ShouldBeNear(At(filled, 5, 5), 255, 0, 0, 128);

        ColorBitmap transferred = Draw(Filter.Replace("STEP", "<feComponentTransfer><feFuncR type=\"linear\" slope=\"2\" intercept=\"0.1\"/><feFuncG type=\"table\" tableValues=\"1 0\"/>" +
            "<feFuncB type=\"discrete\" tableValues=\"0.25 0.5 0.75 1\"/><feFuncA type=\"gamma\" amplitude=\"0.5\" exponent=\"2\" offset=\"0.1\"/></feComponentTransfer>"));
        // Red 0.2 -> 0.5; green 0.4 -> 0.6; blue 0.6 is in the third quarter -> 0.75; alpha 1 -> 0.6.
        ShouldBeNear(At(transferred, 20, 20), 128, 153, 191, 153);
        // Alpha 0 -> 0.1, with whatever colour the functions make of none: red 0.1, green 1, blue 0.25.
        ShouldBeNear(At(transferred, 5, 5), 26, 255, 64, 26, within: 6);
    }

    [Fact]
    public void ADropShadow_IsTheShapesAlpha_Blurred_Moved_AndColoured_UnderTheShape()
    {
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feDropShadow dx=\"6\" dy=\"6\" stdDeviation=\"0.01\" flood-color=\"#0000ff\" flood-opacity=\"0.5\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"#ff0000\" filter=\"url(#f)\"/>");
        At(bitmap, 12, 12).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));         // the shape, on top
        At(bitmap, 18, 18).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255));         // and where its shadow is under it
        ShouldBeNear(At(bitmap, 23, 23), 0, 0, 255, 128);                              // the shadow alone, in its own colour
        At(bitmap, 27, 27).A.ShouldBe((byte)0);
    }

    [Fact]
    public void AFilter_ThenAClip_ThenOpacity()
    {
        // Moved by the filter to 20..30, then cut to the clip's 20..25, then faded to a half.
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feOffset dx=\"10\"/></filter><clipPath id=\"c\"><rect x=\"20\" width=\"5\" height=\"40\"/></clipPath>" +
            "<rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"#000\" filter=\"url(#f)\" clip-path=\"url(#c)\" opacity=\"0.5\"/>");
        Ink(bitmap).ShouldBe(5 * 10 * 128 / 255.0, 0.01);
        At(bitmap, 22, 15).A.ShouldBe((byte)128);
        At(bitmap, 15, 15).A.ShouldBe((byte)0);
        At(bitmap, 27, 15).A.ShouldBe((byte)0);
    }

    [Fact]
    public void ATurnedElement_IsFilteredAlongItsOwnAxes()
    {
        // A quarter turn: "across" for the element is down the picture. The square 10..20 moved by 8 along its own x.
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feOffset dx=\"8\"/></filter><g transform=\"rotate(90 20 20)\"><rect x=\"10\" y=\"15\" width=\"10\" height=\"10\" fill=\"#000\" filter=\"url(#f)\"/></g>");
        Ink(bitmap).ShouldBe(100, 1.0);
        At(bitmap, 20, 23).A.ShouldBe((byte)255);
        At(bitmap, 20, 14).A.ShouldBe((byte)0);
        At(bitmap, 20, 30).A.ShouldBe((byte)0);
        // And a turn that is not a quarter is resampled, which keeps the ink and the place.
        ColorBitmap turned = Draw($"<filter id=\"f\" {Wide}><feOffset dx=\"8\"/></filter><g transform=\"rotate(30 20 20)\"><rect x=\"6\" y=\"15\" width=\"10\" height=\"10\" fill=\"#000\" filter=\"url(#f)\"/></g>");
        Ink(turned).ShouldBe(100, 1.5);
        At(turned, 19, 20).A.ShouldBe((byte)255);
    }

    // Three the mutation check found nothing looking at.
    [Fact]
    public void SourceAlpha_IsTheLayersAlpha_WithNoColour()
    {
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feOffset in=\"SourceAlpha\"/></filter><rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"#c00\" filter=\"url(#f)\"/>");
        At(bitmap, 15, 15).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255));
        Ink(bitmap).ShouldBe(100, 1e-9);
    }

    [Fact]
    public void ABlendStep_PutsItsFirstPictureOnItsSecond()
    {
        // "normal" is the one mode that shows which is on top without any arithmetic: the square on the flood.
        ColorBitmap bitmap = Draw($"<filter id=\"f\" {Wide}><feFlood flood-color=\"#f00\" result=\"r\"/><feBlend in=\"SourceGraphic\" in2=\"r\" mode=\"normal\"/></filter>" +
            "<rect x=\"10\" y=\"10\" width=\"10\" height=\"10\" fill=\"#00f\" filter=\"url(#f)\"/>");
        ShouldBeNear(At(bitmap, 15, 15), 0, 0, 255, 255, what: "the square, on top");
        ShouldBeNear(At(bitmap, 5, 5), 255, 0, 0, 255, what: "the flood, where the square is not");
    }

    [Fact]
    public void ATurnedFilter_AlsoHasTheLayerFromOutsideItsArea()
    {
        // As NothingIsDrawnOutsideTheFiltersArea_ButTheLayerOutsideItStillCounts, turned: the area is the square 10..20,
        // the layer a bar across everything, moved 5 along itself. What comes into the area on its left was outside it.
        ColorBitmap bitmap = Draw("<filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10\" y=\"10\" width=\"10\" height=\"10\"><feOffset dx=\"5\"/></filter>" +
            "<g transform=\"rotate(30 15 15)\"><rect x=\"-20\" y=\"12\" width=\"80\" height=\"6\" fill=\"#000\" filter=\"url(#f)\"/></g>");
        Ink(bitmap).ShouldBe(10 * 6, 2.0);
        // 1.2 units inside the area's left edge, in the middle of the bar.
        At(bitmap, 11, 13).A.ShouldBe((byte)255);
    }

    [Fact]
    public void AFilterWithNoSteps_OrNoArea_DrawsNothing_AndOneBuiltWrong_DoesNotThrow()
    {
        static VectorScene With(VectorFilter filter) => new(40, 40,
        [
            new VectorGroup
            {
                Opacity = 1, Filter = filter,
                Children = [new VectorShape { Path = Square(), Fill = new VectorFill(new SolidPaint(new Rgba32(0, 0, 0)), 1, FillRule.NonZero) }],
            },
        ]);
        var all = new VectorRect(0, 0, 40, 40);
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = all, Primitives = [] }), 40, 40)).ShouldBe(0);
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = new VectorRect(0, 0, 0, 40), Primitives = [new FilterOffset(VectorFilterInput.SourceGraphic, 0, 0) { Region = all }] }), 40, 40)).ShouldBe(0);
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = all, Transform = default, Primitives = [new FilterOffset(VectorFilterInput.SourceGraphic, 0, 0) { Region = all }] }), 40, 40)).ShouldBe(0);
        // A step that refers to itself, or to one that is not there: a scene file refuses that, a scene built by hand may hold it. The layer is used.
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = all, Primitives = [new FilterOffset(VectorFilterInput.Result(0), 0, 0) { Region = all }] }), 40, 40)).ShouldBe(100, 1e-9);
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = all, Primitives = [new FilterMerge([VectorFilterInput.Result(9), VectorFilterInput.Result(-1)]) { Region = all }] }), 40, 40)).ShouldBe(100, 1e-9);
        // The step's own area is where its picture is: outside it, nothing.
        Ink(SceneRasterizer.Rasterize(With(new VectorFilter { Region = all, Primitives = [new FilterOffset(VectorFilterInput.SourceGraphic, 0, 0) { Region = new VectorRect(10, 10, 5, 10) }] }), 40, 40)).ShouldBe(50, 1e-9);

        static VectorPath Square()
        {
            var builder = new PathBuilder();
            builder.MoveTo(10, 10);
            builder.LineTo(20, 10);
            builder.LineTo(20, 20);
            builder.LineTo(10, 20);
            builder.Close();
            return builder.ToPath();
        }
    }
}
