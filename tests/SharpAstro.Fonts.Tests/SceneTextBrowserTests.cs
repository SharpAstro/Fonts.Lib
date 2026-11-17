using System.Text;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The text of an SVG document, read by <c>SharpAstro.Svg</c>, laid out by <see cref="SceneText"/> and drawn by
/// <see cref="SceneRasterizer"/>, held against headless Chrome or Edge drawing the same document in the same font.
/// </summary>
/// <remarks>
/// <para><b>The font is the page's own</b>: DejaVu Sans, given to the browser as a web font and to
/// <see cref="SceneText.ShaperFor(OpenTypeFont)"/> as the one face, so that nothing depends on what a machine has
/// installed. Only the regular face: a browser asked for bold or italic with no such face makes one up.</para>
/// <para><b>What the page turns off</b>, because the plain shaper here does not do it: ligatures. And what it asks for:
/// <c>text-rendering: geometricPrecision</c>, so that the browser places glyphs where the font says, not on whole
/// pixels.</para>
/// <para>This is where the reader's white-space rules, its lists of positions and its baselines are checked too: the
/// Codecs repository has no font, so its own tests can only say what the scene holds.</para>
/// </remarks>
public sealed class SceneTextBrowserTests
{
    private static readonly Lazy<OpenTypeFont> Font = new(() => OpenTypeFont.Load(File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans))));

    /// <summary>A case: its name, the picture's size, and what is inside the svg element.</summary>
    internal static readonly (string Name, int Width, int Height, string Body)[] Cases =
    [
        ("plain", 300, 60, """
            <text x="10" y="42" font-size="36">Hamburg fonts</text>
            """),
        ("spans", 320, 60, """
            <text x="10" y="40" font-size="30" fill="#204080">one <tspan fill="#c02020" font-size="40">two</tspan> three <tspan fill="#208020" font-size="50%">small</tspan></text>
            """),
        ("anchor", 300, 150, """
            <path d="M150 0 V150" stroke="#f0a000"/>
            <g font-size="28"><text x="150" y="35">start</text><text x="150" y="80" text-anchor="middle">middle of it</text>
            <text x="150" y="125" text-anchor="end">the end</text></g>
            """),
        ("kerning", 300, 60, """
            <text x="10" y="42" font-size="36">AVATAR Wo To Ty.</text>
            """),
        ("white-space", 340, 200, """
            <g font-size="24">
            <text x="10" y="30">   leading,   inner
               and line   ends   </text><text x="330" y="30" text-anchor="end">|</text>
            <text x="10" y="70" xml:space="preserve">  kept   spaces  </text>
            <text x="10" y="110">a <tspan fill="#c00"> b </tspan> <tspan> </tspan> c<tspan> d</tspan></text>
            <text x="170" y="150" text-anchor="middle">  centred after trimming  </text>
            <text x="170" y="190" text-anchor="middle" xml:space="preserve">  kept, so wider  </text>
            </g><path d="M170 120 V200" stroke="#f0a000"/>
            """),
        ("positions", 320, 200, """
            <g font-size="26">
            <text x="10 40 70 100 130 160" y="30">spread</text>
            <text x="10" y="70" dx="0 6 6 6" dy="0 -6 12 -6">shifted along</text>
            <text x="10" y="110" rotate="0 20 40 60 -30">turning now</text>
            <text x="20" dx="15" y="150">x and dx</text>
            <text x="10" y="190">abc<tspan x="120" y="180" fill="#c00">def<tspan dy="8" fill="#06c">ghi</tspan></tspan>jkl</text>
            </g>
            """),
        ("chunks", 320, 110, """
            <path d="M100 0 V110 M240 0 V110" stroke="#f0a000"/>
            <g font-size="24" text-anchor="middle"><text x="100" y="35">one<tspan x="240">two</tspan></text>
            <text x="100 240" y="75">A<tspan fill="#c00">BC</tspan>D</text></g>
            <text x="100" y="100" font-size="18" text-anchor="end">end <tspan x="240" text-anchor="start">start</tspan></text>
            """),
        ("spacing", 320, 110, """
            <g font-size="24"><text x="10" y="30" letter-spacing="4">letter spacing</text>
            <text x="10" y="65" word-spacing="16">word spacing here</text>
            <text x="310" y="100" text-anchor="end" letter-spacing="-1.5" word-spacing="0.5em">tight and wide</text></g>
            """),
        ("baselines", 420, 140, """
            <path d="M0 40 H420 M0 110 H420" stroke="#f0a000"/>
            <g font-size="26"><text y="40"><tspan x="5">Ap</tspan><tspan x="60" dominant-baseline="middle">Ap</tspan>
            <tspan x="115" dominant-baseline="central">Ap</tspan><tspan x="170" dominant-baseline="hanging">Ap</tspan>
            <tspan x="225" dominant-baseline="mathematical">Ap</tspan><tspan x="280" dominant-baseline="text-before-edge">Ap</tspan>
            <tspan x="335" dominant-baseline="text-after-edge">Ap</tspan></text>
            <text x="5" y="110">low<tspan baseline-shift="10" fill="#c00">up<tspan baseline-shift="-50%" fill="#06c">down</tspan></tspan>
            <tspan dominant-baseline="ideographic">ideo</tspan> <tspan alignment-baseline="middle">mid</tspan></text></g>
            """),
        ("lines", 340, 250, """
            <g font-size="28"><text x="10" y="35" text-decoration="underline">under <tspan fill="#c00">lined</tspan> text</text>
            <text x="10" y="80"><tspan text-decoration="overline">over</tspan> <tspan text-decoration="line-through">through</tspan>
            <tspan text-decoration="underline overline" font-size="18">both</tspan></text>
            <text x="10" y="130" font-size="44" text-decoration="underline" fill="#204080">gypsy jumping</text>
            <text x="10" y="180" text-decoration="underline line-through" fill="#208020">small <tspan font-size="44" fill="#c02020">Big</tspan>
            <tspan font-size="14" text-decoration="overline" fill="#202020">tiny, quite</tspan></text>
            <text x="10" y="230" font-size="20" text-decoration="underline" letter-spacing="3" fill="#602080">spaced <tspan dy="-8">raised</tspan>
            <tspan dy="8" rotate="15">turned</tspan> end</text></g>
            """),
        ("scripts", 340, 130, """
            <g font-size="30"><text x="10" y="50">x<tspan baseline-shift="super" font-size="16">2</tspan> + y<tspan baseline-shift="sub" font-size="16">i<tspan baseline-shift="sub" font-size="10">k</tspan></tspan>
            = H<tspan baseline-shift="sub">2</tspan>O<tspan baseline-shift="super">n</tspan></text>
            <text x="10" y="105" baseline-shift="super" fill="#06c">all of it <tspan baseline-shift="sub" fill="#c00">and back</tspan> <tspan baseline-shift="25%">a quarter</tspan></text></g>
            <path d="M0 105 H340" stroke="#f0a000"/>
            """),
        ("text-length", 320, 150, """
            <path d="M10 0 V150 M300 0 V150" stroke="#f0a000"/>
            <g font-size="24"><text x="10" y="30">natural length</text>
            <text x="10" y="65" textLength="290">spaced to fit</text>
            <text x="10" y="100" textLength="290" lengthAdjust="spacingAndGlyphs">stretched to fit</text>
            <text x="10" y="135" textLength="120" lengthAdjust="spacingAndGlyphs">squeezed to fit</text></g>
            """),
        ("path", 340, 230, """
            <defs><path id="wave" d="M 20 80 C 80 10 140 150 200 80 S 300 20 320 90"/>
            <path id="line" d="M 30 200 L 300 150" transform="translate(0 10)"/></defs>
            <use href="#wave" fill="none" stroke="#f0a000"/><use href="#line" stroke="#f0a000"/>
            <g font-size="20"><text><textPath href="#wave">text set along a wavy path</textPath></text>
            <text fill="#06c"><textPath href="#wave" startOffset="50%" text-anchor="middle"><tspan dy="22">centred below it</tspan></textPath></text>
            <text><textPath href="#line" startOffset="20">along a <tspan fill="#c00" dy="-6">line</tspan> and off the end of it</textPath> and on</text></g>
            """),
        // Closed paths: the text stops where the path ends, started part of the way round or not. Straight pieces
        // only, and that is deliberate. A browser measures a path along chords that may lie half a unit off the curve,
        // so round a circle its text runs ahead of the true length: by about half a pixel three quarters round a circle
        // of radius 40, and still enough on one of radius 80 to move the tips of tall letters a pixel (both measured).
        // That is the browser's approximation, not something to copy, so circles are not held against it.
        ("closed-paths", 420, 240, """
            <defs><path id="there" d="M 20 30 H 190"/><path id="back" d="M 20 80 H 190 Z"/>
            <path id="square" d="M 30 120 h 100 v 100 h -100 z"/><path id="kite" d="M 320 110 L 400 170 L 320 230 L 240 170 Z"/></defs>
            <g font-size="16"><text><textPath href="#there">an open path is left at its end</textPath></text>
            <text><textPath href="#back">there and back again and no more</textPath></text>
            <text><textPath href="#square">round the square it goes, round the corners, and stops</textPath></text>
            <text><textPath href="#kite" startOffset="30%">started part of the way round, it stops at the end</textPath></text></g>
            """),
        // What the specification leaves to be read two ways, or what a reader could easily get wrong, one case a row.
        ("edges", 420, 400, """
            <defs><path id="rail" d="M 20 215 H 400"/><path id="rail2" d="M 20 255 H 400"/></defs>
            <path d="M210 0 V190" stroke="#f0a000"/><use href="#rail" stroke="#f0a000"/><use href="#rail2" stroke="#f0a000"/>
            <g font-size="20">
            <text x="10 40 70 100 130 160 190" y="22">   ab<tspan display="none">XYZ</tspan>c<tspan> </tspan> <tspan>d</tspan>e</text>
            <text x="10" y="52" xml:space="preserve">a  <tspan xml:space="default">  b   c  </tspan>  d</text>
            <text x="10" y="82" style="white-space: pre">pre   kept</text><text x="210" y="82" xml:space="preserve" style="white-space: normal">  css   wins  </text>
            <text x="210" y="112" text-anchor="middle">mid<tspan text-anchor="end" fill="#c00">dle</tspan> <tspan x="210" dy="26" text-anchor="end">end<tspan text-anchor="start" fill="#06c">ing</tspan></tspan></text>
            <text x="10" y="172" dy="-4" rotate="20">ab<tspan dy="6 0 -6" rotate="-20 0">cdef</tspan>gh</text>
            <text x="300" y="172" dominant-baseline="middle" font-size="14">mid<tspan font-size="26">Big</tspan><tspan dominant-baseline="auto">auto</tspan></text>
            <text x="50" y="100"><textPath href="#rail" x="30" y="50">x moves<tspan x="200" y="90" fill="#c00">along</tspan><tspan dy="-8" fill="#06c">up</tspan></textPath></text>
            <text x="50" y="300" text-anchor="middle"><textPath href="#rail2" startOffset="50%">in the middle</textPath></text>
            <text x="20" y="290" textLength="380" text-anchor="middle">long<tspan fill="#c00"> and </tspan>anchored</text>
            <text x="210" y="320" textLength="200" text-anchor="middle">centred length</text>
            <text x="10" y="350" opacity="0.5">half <tspan opacity="0.5">quarter</tspan> <tspan fill-opacity="0.5" fill="#06c">fill</tspan></text>
            <text x="10" y="385" letter-spacing="2" text-decoration="underline">gaps <tspan letter-spacing="8">wide</tspan> j</text>
            <text x="410" y="385" text-anchor="end" word-spacing="10" font-size="16">end with spaces</text>
            </g>
            """),
        ("paint", 320, 170, """
            <defs><linearGradient id="box"><stop offset="0" stop-color="#c02020"/><stop offset="1" stop-color="#2040c0"/></linearGradient>
            <linearGradient id="user" gradientUnits="userSpaceOnUse" x1="0" y1="60" x2="0" y2="100"><stop offset="0" stop-color="#f0c000"/><stop offset="1" stop-color="#108030"/></linearGradient></defs>
            <g font-size="40"><text x="10" y="45" fill="url(#box)">box <tspan font-size="24">gradient</tspan></text>
            <text x="10" y="100" fill="url(#user)" stroke="#202020" stroke-width="1.5">user units</text>
            <text x="10" y="155" fill="#f0c000" stroke="#204080" stroke-width="6" stroke-linejoin="round" paint-order="stroke" opacity="0.7">stroke first</text></g>
            """),
        ("transform", 300, 160, """
            <text transform="translate(30 60) rotate(-15) scale(1.4 1)" font-size="26">turned and wide</text>
            <g transform="translate(150 130) skewX(-20)" font-size="2em"><text text-anchor="middle" y="0.2em" dy="-0.2em">slanted</text></g>
            """),
        ("hidden", 300, 100, """
            <g font-size="26"><text x="10" y="35">here <tspan visibility="hidden">not seen</tspan> there</text>
            <text x="10" y="80">here <tspan display="none">not there</tspan> there</text></g>
            """),
        ("font", 320, 110, """
            <text x="10" y="40" style="font: 34px T">shorthand</text>
            <text x="10" y="90" font-size="x-large">x-large <tspan font-size="larger">larger</tspan> <tspan font-size="small">small</tspan> <tspan font-size="1.5em">1.5em</tspan></text>
            """),
    ];

    public static TheoryData<string> Names
    {
        get
        {
            var names = new TheoryData<string>();
            foreach ((string name, _, _, _) in Cases) names.Add(name);
            return names;
        }
    }

    private static readonly Lazy<Dictionary<string, byte[]>> Rendered = new(RenderAll);

    private static string Document(int width, int height, string body) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\">{body}</svg>";

    [Theory]
    [MemberData(nameof(Names))]
    public void Text_IsDrawnAsTheBrowserDrawsIt(string name)
    {
        if (SceneRasterizerBrowserTests.Browser.Path is null)
        {
            Assert.Skip($"No Chromium-family browser found. Set {SceneRasterizerBrowserTests.Browser.Variable} to its executable, or install Chrome or Edge.");
            return;
        }
        (_, int width, int height, string body) = Cases.Single(one => one.Name == name);

        SvgDocument document = SvgReader.Parse(Document(width, height, body));
        document.Issues.ShouldBeEmpty(name);
        var options = new SceneRenderOptions { ShapeText = SceneText.ShaperFor(Font.Value) };
        ColorBitmap ours = SceneRasterizer.Rasterize(document.Scene, width, height, System.Numerics.Matrix3x2.Identity, options);
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int a = ours.Pixels[i * 4 + 3];
            for (int ch = 0; ch < 3; ch++) rgb[i * 3 + ch] = (byte)((ours.Pixels[i * 4 + ch] * a + 255 * (255 - a) + 127) / 255);
        }
        byte[] theirs = Rendered.Value[name];

        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            PngWriter.WriteRgba(Path.Combine(dump, $"text-{name}-ours.png"), Opaque(rgb), width, height);
            PngWriter.WriteRgba(Path.Combine(dump, $"text-{name}-browser.png"), Opaque(theirs), width, height);
        }

        double sum = 0;
        int wrong = 0;
        var where = new StringBuilder();
        for (int i = 0; i < width * height; i++)
        {
            int worst = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                int d = Math.Abs(rgb[i * 3 + ch] - theirs[i * 3 + ch]);
                sum += d;
                worst = Math.Max(worst, d);
            }
            if (worst > SceneRasterizerBrowserTests.MaxPixelDifference && SceneRasterizerBrowserTests.OutsideNeighbourhood(rgb, theirs, width, height, i) && wrong++ < 6)
            {
                where.Append($" ({i % width},{i / width}: ours {rgb[i * 3]},{rgb[i * 3 + 1]},{rgb[i * 3 + 2]} theirs {theirs[i * 3]},{theirs[i * 3 + 1]},{theirs[i * 3 + 2]})");
            }
        }
        double mean = sum / (width * height * 3);
        string report = $"{name}: mean {mean:0.000}, pixels off {wrong} of {width * height}{where}";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        theirs.AsSpan().ContainsAnyExcept((byte)255).ShouldBeTrue($"{name}: the browser drew nothing");
        mean.ShouldBeLessThan(MaxMeanDifference, report);
        wrong.ShouldBe(0, report);

        static byte[] Opaque(byte[] rgb)
        {
            byte[] rgba = new byte[rgb.Length / 3 * 4];
            for (int i = 0; i < rgb.Length / 3; i++)
            {
                rgb.AsSpan(i * 3, 3).CopyTo(rgba.AsSpan(i * 4));
                rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }
    }

    /// <summary>Of 255, over all pixels and channels. Text is all edges, so more of a picture is anti-aliasing than in a drawing of shapes.</summary>
    private const double MaxMeanDifference = 2.5;

    private static Dictionary<string, byte[]> RenderAll()
    {
        const int gap = 4;
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
        html.Append($"@font-face {{ font-family: T; src: url(data:font/ttf;base64,{Convert.ToBase64String(File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans)))}); }}");
        html.Append("html, body { margin: 0; background: #fff; } svg { position: absolute; left: 0; } ");
        html.Append("text { font-family: T; font-variant-ligatures: none; text-rendering: geometricPrecision; }");
        html.Append("</style></head><body>");
        int y = 0, pageWidth = 1;
        var tops = new List<int>();
        foreach ((_, int width, int height, string body) in Cases)
        {
            html.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" style=\"top:{y}px\">{body}</svg>");
            tops.Add(y);
            y += height + gap;
            pageWidth = Math.Max(pageWidth, width + gap);
        }
        html.Append("</body></html>");
        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "text-page.html"), html.ToString());
        }

        byte[] page = SceneRasterizerBrowserTests.Browser.Capture(html.ToString(), pageWidth, y, scale: 4);
        var rendered = new Dictionary<string, byte[]>();
        for (int n = 0; n < Cases.Length; n++)
        {
            (string name, int width, int height, _) = Cases[n];
            byte[] rgb = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++)
                {
                    page.AsSpan(((tops[n] + row) * pageWidth + x) * 4, 3).CopyTo(rgb.AsSpan((row * width + x) * 3));
                }
            }
            rendered[name] = rgb;
        }
        return rendered;
    }
}
