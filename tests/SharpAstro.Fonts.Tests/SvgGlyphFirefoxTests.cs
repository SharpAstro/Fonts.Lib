using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using SharpAstro.Png;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The glyphs of an OpenType 'SVG ' table, drawn here and by Firefox from the same font bytes.
/// </summary>
/// <remarks>
/// <para><b>Firefox, because Chrome and Edge do not draw OpenType SVG glyphs at all</b> (they draw the outline). The
/// font is <see cref="SvgGlyphFont"/>; the page sets each case as SVG text, whose <c>y</c> is the baseline exactly,
/// where an HTML line's baseline depends on which of the font's metrics a platform takes.</para>
/// <para><b>Firefox's capture is committed</b> (<c>Baselines/svg-glyphs-firefox.png</c>), so the comparison runs
/// everywhere with nothing installed. Where Firefox is found a second test captures the page again and holds the
/// committed picture to it, so that it cannot drift from what Firefox draws; <c>BASELINE_REGEN=1</c> rewrites it. The
/// font is built in memory from a fixture and a table written in the tests, so it is the same bytes on every run.</para>
/// <para><b>Where Firefox does not do what the specification says</b> (measured, Firefox 160 on Windows), and so cannot
/// be asked; <see cref="SvgGlyphBrowserTests"/> holds these against the specification's own definition:</para>
/// <list type="bullet">
/// <item>it defines no <c>--colorN</c> for a 'CPAL' palette, with or without <c>font-palette</c>: every <c>var()</c>
/// takes its fallback. The Firefox cases are therefore drawn here with no palette either.</item>
/// <item><c>currentColor</c> is black whatever the colour and the fill of the text. The page's text is black.</item>
/// <item>it draws no stroke in a glyph: not on a filled shape and not alone.</item>
/// <item>a glyph that is not the root element inherits from the groups it stands in, and the root's <c>viewBox</c>
/// does not apply to it. The specification draws it as a <c>use</c>, which takes neither from where the element
/// stands and is inside the root's <c>viewBox</c> mapping. The transforms of those groups are not applied by either.</item>
/// </list>
/// </remarks>
public sealed class SvgGlyphFirefoxTests
{
    /// <summary>Pixels an em: 32 font units are one pixel.</summary>
    internal const int Size = 64;

    /// <summary>A cell of the page, and where the pen and the baseline are in it.</summary>
    internal const int CellWidth = 96, CellHeight = 96, PenX = 24, Baseline = 64;

    private const string BaselineName = "svg-glyphs-firefox.png";

    private static SvgGlyphFont.Case[] FirefoxCases => [.. SvgGlyphFont.Cases.Where(one => one.Firefox)];

    public static TheoryData<char, string> Cells
    {
        get
        {
            var cells = new TheoryData<char, string>();
            foreach (SvgGlyphFont.Case one in FirefoxCases) cells.Add(one.Letter, one.About);
            return cells;
        }
    }

    /// <summary>The committed capture: RGBA, one row of the Firefox cases.</summary>
    private static readonly Lazy<byte[]?> Committed = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Baselines", BaselineName);
        if (!File.Exists(path)) return null;
        PngImageDecoder.TryDecodeRgba8(File.ReadAllBytes(path), out byte[]? rgba, out int width, out int height).ShouldBeTrue();
        (width, height).ShouldBe((FirefoxCases.Length * CellWidth, CellHeight), "the committed capture is of another set of cases: regenerate it");
        return rgba;
    });

    [Theory]
    [MemberData(nameof(Cells))]
    public void AGlyph_IsDrawnAsFirefoxDrawsIt(char letter, string about)
    {
        byte[]? committed = Committed.Value;
        committed.ShouldNotBeNull($"Baselines/{BaselineName} is missing: run the tests with BASELINE_REGEN=1 where Firefox is installed.");
        int column = Array.FindIndex(FirefoxCases, one => one.Letter == letter);
        byte[] theirs = Cell(committed, 4, CellHeight, column, 0, FirefoxCases.Length);
        // Black text and no palette: what Firefox gives a glyph (see the remarks).
        byte[] ours = OurCell(letter, new Rgba32(0, 0, 0, 255), palette: -1);
        Compare($"'{letter}' ({about})", ours, theirs);
    }

    [Fact]
    public void TheCommittedCapture_IsWhatFirefoxDraws()
    {
        if (Firefox.Path is null)
        {
            Assert.Skip($"Firefox not found. Set {Firefox.Variable} to its executable. The other tests here use the committed capture.");
            return;
        }

        int width = FirefoxCases.Length * CellWidth;
        byte[] live = Firefox.Capture(Page(), width, CellHeight);
        if (Environment.GetEnvironmentVariable("BASELINE_REGEN") == "1" || Committed.Value is null)
        {
            string path = Path.Combine(SourceDirectory(), "Baselines", BaselineName);
            File.WriteAllBytes(path, SharpAstro.Png.PngWriter.Encode(live, width, CellHeight));
            Assert.Skip($"Wrote {path}. Look at it, then run again.");
            return;
        }

        // The same page from the same font: only what a newer Firefox draws differently may differ.
        byte[] committed = Committed.Value;
        for (int column = 0; column < FirefoxCases.Length; column++)
        {
            Compare($"'{FirefoxCases[column].Letter}', committed against live Firefox",
                Cell(committed, 4, CellHeight, column, 0, FirefoxCases.Length), Cell(live, 4, CellHeight, column, 0, FirefoxCases.Length));
        }
    }

    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    /// <summary>The page Firefox is shown: every Firefox case, each letter alone in its cell, black.</summary>
    internal static string Page()
    {
        int width = FirefoxCases.Length * CellWidth;
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
        html.Append($"@font-face {{ font-family: T; src: url(data:font/ttf;base64,{Convert.ToBase64String(SvgGlyphFont.Bytes)}); }}");
        html.Append($"html, body {{ margin: 0; background: #fff; }} svg {{ display: block; }} text {{ font-family: T; font-size: {Size}px; fill: #000; }}");
        html.Append("</style></head><body>");
        html.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{CellHeight}\">");
        for (int i = 0; i < FirefoxCases.Length; i++)
        {
            // Each letter in a viewport of its own, so that what leaves its cell does not land in the next.
            html.Append($"<svg x=\"{i * CellWidth}\" y=\"0\" width=\"{CellWidth}\" height=\"{CellHeight}\"><text x=\"{PenX}\" y=\"{Baseline}\">{FirefoxCases[i].Letter}</text></svg>");
        }
        html.Append("</svg></body></html>");
        return html.ToString();
    }

    /// <summary>One cell of a captured page, as RGB.</summary>
    internal static byte[] Cell(byte[] page, int bytesPerPixel, int pageHeight, int column, int row, int columns = -1)
    {
        int pageWidth = (columns < 0 ? SvgGlyphFont.Cases.Length : columns) * CellWidth;
        page.Length.ShouldBeGreaterThanOrEqualTo(pageWidth * pageHeight * bytesPerPixel);
        byte[] rgb = new byte[CellWidth * CellHeight * 3];
        for (int y = 0; y < CellHeight; y++)
        {
            for (int x = 0; x < CellWidth; x++)
            {
                page.AsSpan(((row * CellHeight + y) * pageWidth + column * CellWidth + x) * bytesPerPixel, 3).CopyTo(rgb.AsSpan((y * CellWidth + x) * 3));
            }
        }
        return rgb;
    }

    /// <summary>Our drawing of one cell: the glyph rendered from the font and put down at the pen, over white. RGB.</summary>
    internal static byte[] OurCell(char letter, Rgba32 colour, int palette)
    {
        byte[] rgb = new byte[CellWidth * CellHeight * 3];
        rgb.AsSpan().Fill(255);
        ColorBitmap? drawn = SvgGlyphRenderer.TryRender(SvgGlyphFont.Font, SvgGlyphFont.GlyphOf(letter), Size, colour, palette);
        drawn.ShouldNotBeNull($"the glyph for '{letter}'");
        int left = PenX + drawn.Left, top = Baseline - drawn.Top;
        for (int y = 0; y < drawn.Height; y++)
        {
            for (int x = 0; x < drawn.Width; x++)
            {
                int px = left + x, py = top + y;
                if (px < 0 || py < 0 || px >= CellWidth || py >= CellHeight) continue;
                int a = drawn.Pixels[(y * drawn.Width + x) * 4 + 3];
                for (int ch = 0; ch < 3; ch++)
                {
                    int at = (py * CellWidth + px) * 3 + ch;
                    rgb[at] = (byte)((drawn.Pixels[(y * drawn.Width + x) * 4 + ch] * a + rgb[at] * (255 - a) + 127) / 255);
                }
            }
        }
        return rgb;
    }

    /// <summary>
    /// Two drawings of a cell, held as <see cref="SceneRasterizerBrowserTests"/> holds a document: the mean small, and no
    /// pixel far off that is also outside what the other has within a pixel of it (an edge may sit a pixel away, a
    /// shape in the wrong place may not).
    /// </summary>
    internal static void Compare(string name, byte[] ours, byte[] theirs)
    {
        double sum = 0;
        int wrong = 0;
        var where = new StringBuilder();
        for (int i = 0; i < CellWidth * CellHeight; i++)
        {
            int worst = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                int d = Math.Abs(ours[i * 3 + ch] - theirs[i * 3 + ch]);
                sum += d;
                worst = Math.Max(worst, d);
            }
            if (worst > SceneRasterizerBrowserTests.MaxPixelDifference
                && SceneRasterizerBrowserTests.OutsideNeighbourhood(ours, theirs, CellWidth, CellHeight, i) && wrong++ < 6)
            {
                where.Append($" ({i % CellWidth},{i / CellWidth}: ours {ours[i * 3]},{ours[i * 3 + 1]},{ours[i * 3 + 2]} theirs {theirs[i * 3]},{theirs[i * 3 + 1]},{theirs[i * 3 + 2]})");
            }
        }
        double mean = sum / (CellWidth * CellHeight * 3);
        string report = $"{name}: mean {mean:0.000}, pixels off {wrong} of {CellWidth * CellHeight}{where}";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        // A cell that is all white on both sides agrees about nothing.
        theirs.AsSpan().ContainsAnyExcept((byte)255).ShouldBeTrue($"{name}: nothing was drawn in the reference");
        mean.ShouldBeLessThan(SceneRasterizerBrowserTests.MaxMeanDifference, report);
        wrong.ShouldBe(0, report);
    }

    /// <summary>Finds Firefox and has it capture a page, headless. RGBA bytes, <c>width</c> wide.</summary>
    private static class Firefox
    {
        public const string Variable = "SVG_FIREFOX";

        public static readonly string? Path = Resolve();

        public static byte[] Capture(string html, int width, int height)
        {
            string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpastro-fonts-svg-glyphs-" + Guid.NewGuid().ToString("N"));
            string profile = System.IO.Path.Combine(work, "profile");
            Directory.CreateDirectory(profile);
            try
            {
                string page = System.IO.Path.Combine(work, "page.html"), shot = System.IO.Path.Combine(work, "page.png");
                File.WriteAllText(page, html);
                // A profile of its own, so that a Firefox the person has open is left alone, and one CSS pixel a
                // device pixel whatever the screen's scaling is. Colour management off: the bytes are sRGB as written.
                File.WriteAllText(System.IO.Path.Combine(profile, "user.js"),
                    """
                    user_pref("layout.css.devPixelsPerPx", "1.0");
                    user_pref("gfx.color_management.mode", 0);
                    user_pref("browser.shell.checkDefaultBrowser", false);
                    user_pref("datareporting.policy.dataSubmissionEnabled", false);
                    user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
                    user_pref("app.update.enabled", false);
                    """);
                var start = new ProcessStartInfo(Path!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string argument in (string[])
                    ["--headless", "--no-remote", "--profile", profile, $"--window-size={width},{height}", "--screenshot", shot, new Uri(page).AbsoluteUri])
                {
                    start.ArgumentList.Add(argument);
                }

                // Firefox exits when the screenshot is written. The wait is on that exit, with a deadline; a Firefox
                // that outlives it is ended with everything it started.
                var log = new StringBuilder();
                using var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (log) log.AppendLine(e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    bool exited = process.WaitForExit(TimeSpan.FromSeconds(90));
                    if (!exited || !File.Exists(shot))
                    {
                        string said;
                        lock (log) said = log.ToString();
                        throw new InvalidOperationException($"{Path} made no screenshot. {said}");
                    }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(TimeSpan.FromSeconds(10));
                    }
                }

                PngImageDecoder.TryDecodeRgba8(File.ReadAllBytes(shot), out byte[]? rgba, out int w, out int h).ShouldBeTrue();
                w.ShouldBe(width);
                h.ShouldBeGreaterThanOrEqualTo(height);
                return rgba.AsSpan(0, width * height * 4).ToArray();
            }
            finally
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The executable <see cref="Variable"/> names, or Firefox where Windows and macOS install it. On Linux only the
        /// variable: a build machine's Firefox is often a sandboxed package that cannot write the screenshot where it
        /// is asked to, and the committed capture is what a build is held to.
        /// </summary>
        private static string? Resolve()
        {
            if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named) return File.Exists(named) ? named : null;

            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (string? root in (string?[])
                    [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData")])
                {
                    if (!string.IsNullOrEmpty(root)) candidates.Add(System.IO.Path.Combine(root, "Mozilla Firefox", "firefox.exe"));
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add("/Applications/Firefox.app/Contents/MacOS/firefox");
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
