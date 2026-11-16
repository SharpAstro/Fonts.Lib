using System.Diagnostics;
using System.Text;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Png;
using SharpAstro.Svg;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// <see cref="SceneRasterizer"/> against a browser: an SVG document read by <c>SharpAstro.Svg</c> and drawn here, held
/// pixel for pixel against the same document drawn by headless Chrome or Edge.
/// </summary>
/// <remarks>
/// <para>This is where STROKES get checked. The reader's own tests (in the Codecs repo) compare fills with a browser
/// through a test-only rasteriser that has no stroker; this is the real one.</para>
/// <para>All documents go on one page and are captured in one launch. The comparison lets an edge sit one pixel away
/// (a browser's own curves are a little coarse) and nothing else: a pixel is wrong when it is far from the browser's
/// AND outside what the browser has within a pixel of it.</para>
/// <para>Skipped where no browser is found. Set <c>SVG_BROWSER</c> to an executable to name one.</para>
/// </remarks>
public sealed class SceneRasterizerBrowserTests
{
    private const string Stroke = "fill=\"none\" stroke=\"#024\" stroke-width=\"8\"";

    public static TheoryData<string, string, string> Documents => new()
    {
        { "caps", "width=\"120\" height=\"70\"",
            $"<path d=\"M15 15 H105\" {Stroke} stroke-linecap=\"butt\"/><path d=\"M15 35 H105\" {Stroke} stroke-linecap=\"round\"/>" +
            $"<path d=\"M15 55 H105\" {Stroke} stroke-linecap=\"square\"/><path d=\"M60 5 V65\" stroke=\"#c00\" stroke-width=\"1\"/>" },
        { "joins", "width=\"150\" height=\"70\"",
            $"<path d=\"M10 60 L30 15 L50 60\" {Stroke} stroke-linejoin=\"miter\"/><path d=\"M60 60 L80 15 L100 60\" {Stroke} stroke-linejoin=\"round\"/>" +
            $"<path d=\"M110 60 L125 15 L140 60\" {Stroke} stroke-linejoin=\"bevel\"/>" },
        { "miter-limit", "width=\"150\" height=\"70\"",
            // The same sharp corner three times: within the default limit of 4 it is not, so it is bevelled; a limit of 10 lets it through.
            $"<path d=\"M10 60 L25 10 L40 60\" {Stroke}/><path d=\"M60 60 L75 10 L90 60\" {Stroke} stroke-miterlimit=\"10\"/>" +
            $"<path d=\"M105 60 L125 30 L145 60\" {Stroke} stroke-miterlimit=\"1\"/>" },
        { "closed-shapes", "width=\"150\" height=\"70\"",
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"50\" fill=\"#fc0\" stroke=\"#024\" stroke-width=\"6\"/>" +
            "<circle cx=\"80\" cy=\"35\" r=\"20\" fill=\"none\" stroke=\"#a03\" stroke-width=\"5\"/>" +
            "<polygon points=\"115,10 145,35 115,60\" fill=\"#9cf\" stroke=\"#024\" stroke-width=\"4\" stroke-linejoin=\"round\"/>" },
        { "curves", "width=\"150\" height=\"80\"",
            $"<path d=\"M10 70 C 10 10 70 10 70 40 S 130 70 140 15\" {Stroke} stroke-linecap=\"round\"/>" +
            "<path d=\"M10 20 Q 40 -10 60 25 T 110 20\" fill=\"none\" stroke=\"#c60\" stroke-width=\"3\"/>" +
            "<path d=\"M90 70 A 20 10 20 1 1 130 60\" fill=\"none\" stroke=\"#090\" stroke-width=\"4\" stroke-linecap=\"square\"/>" },
        { "dashes", "width=\"150\" height=\"80\"",
            "<path d=\"M10 12 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"12 6\"/>" +
            "<path d=\"M10 28 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"12 6\" stroke-dashoffset=\"9\"/>" +
            "<path d=\"M10 44 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"1 10\" stroke-linecap=\"round\"/>" +
            "<path d=\"M10 60 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"14 4 2 4\" stroke-linecap=\"square\" stroke-dashoffset=\"-5\"/>" +
            "<circle cx=\"125\" cy=\"62\" r=\"12\" fill=\"none\" stroke=\"#a03\" stroke-width=\"3\" stroke-dasharray=\"5 3\"/>" },
        { "stroke-transforms", "width=\"150\" height=\"80\"",
            // A stroke is distorted by a transform that is not uniform, and scaled by one that is.
            "<circle r=\"10\" fill=\"none\" stroke=\"#024\" stroke-width=\"3\" transform=\"translate(40 40) scale(3 1)\"/>" +
            "<rect x=\"-10\" y=\"-10\" width=\"20\" height=\"20\" fill=\"#fc0\" stroke=\"#a03\" stroke-width=\"2\" transform=\"translate(110 40) rotate(25) skewX(20) scale(1.5)\"/>" },
        { "stroke-paints", "width=\"150\" height=\"80\"",
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient></defs>" +
            "<rect x=\"12\" y=\"12\" width=\"50\" height=\"56\" fill=\"#ff8\" stroke=\"url(#g)\" stroke-width=\"10\"/>" +
            "<circle cx=\"110\" cy=\"40\" r=\"24\" fill=\"#09c\" stroke=\"#000\" stroke-width=\"10\" stroke-opacity=\"0.5\"/>" +
            "<path d=\"M80 70 L140 70\" stroke=\"#0a0\" stroke-width=\"8\" opacity=\"0.5\" stroke-linecap=\"round\"/>" },
        { "outline-icon", "width=\"96\" height=\"96\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#123\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"",
            // The kind of icon a stroke-based set is made of.
            "<path d=\"M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z\"/><path d=\"M12 6v6l4 2\"/><path d=\"M3 21l3-3M21 21l-3-3\"/>" },
        { "fills-and-layers", "width=\"150\" height=\"80\"",
            "<defs><radialGradient id=\"r\" fx=\"0.3\" fy=\"0.3\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "<linearGradient id=\"l\" x2=\"0.25\" spreadMethod=\"reflect\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"1\" stop-color=\"#fc0\"/></linearGradient></defs>" +
            "<rect x=\"5\" y=\"5\" width=\"60\" height=\"70\" fill=\"url(#r)\"/><path d=\"M75 5 L145 5 L110 40 Z\" fill=\"url(#l)\"/>" +
            "<path d=\"M110 42 L122 78 L92 56 L128 56 L98 78 Z\" fill=\"#a0a\" fill-rule=\"evenodd\"/>" +
            "<g opacity=\"0.5\"><circle cx=\"30\" cy=\"45\" r=\"22\" fill=\"#e00\"/><circle cx=\"55\" cy=\"45\" r=\"22\" fill=\"#e00\"/></g>" },
    };

    private static readonly Lazy<Dictionary<string, (int Width, int Height, byte[] Rgb)>> Rendered = new(RenderAll);

    [Theory]
    [MemberData(nameof(Documents))]
    public void TheRasteriser_MatchesTheBrowser(string name, string root, string body)
    {
        if (Browser.Path is null)
        {
            Assert.Skip($"No Chromium-family browser found. Set {Browser.Variable} to its executable, or install Chrome or Edge.");
            return;
        }

        SvgDocument document = SvgReader.Parse(Wrap(body, root));
        document.Issues.ShouldBeEmpty(name);
        (int width, int height, byte[] theirs) = Rendered.Value[name];
        ((int)MathF.Round(document.Width), (int)MathF.Round(document.Height)).ShouldBe((width, height));

        ColorBitmap ours = SceneRasterizer.Rasterize(document.Scene, width, height);
        // Over white, as the page is.
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int a = ours.Pixels[i * 4 + 3];
            for (int ch = 0; ch < 3; ch++) rgb[i * 3 + ch] = (byte)((ours.Pixels[i * 4 + ch] * a + 255 * (255 - a) + 127) / 255);
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
            if (worst > MaxPixelDifference && OutsideNeighbourhood(rgb, theirs, width, height, i) && wrong++ < 6)
            {
                where.Append($" ({i % width},{i / width}: ours {rgb[i * 3]},{rgb[i * 3 + 1]},{rgb[i * 3 + 2]} theirs {theirs[i * 3]},{theirs[i * 3 + 1]},{theirs[i * 3 + 2]})");
            }
        }
        double mean = sum / (width * height * 3);
        string report = $"{name}: mean {mean:0.000}, pixels off {wrong} of {width * height}{where}";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            PngWriter.WriteRgba(System.IO.Path.Combine(dump, name + "-ours.png"), ours.Pixels, width, height);
        }
        mean.ShouldBeLessThan(MaxMeanDifference, report);
        wrong.ShouldBe(0, report);
    }

    /// <summary>Of 255. A pixel further off than this is not an anti-aliasing difference.</summary>
    private const int MaxPixelDifference = 72;

    /// <summary>Of 255. How far outside its neighbourhood's range a pixel may be.</summary>
    private const int EdgeSlack = 24;

    /// <summary>Of 255, over all pixels and channels.</summary>
    private const double MaxMeanDifference = 1.2;

    private static bool OutsideNeighbourhood(byte[] ours, byte[] theirs, int width, int height, int i)
    {
        int x = i % width, y = i / width;
        for (int ch = 0; ch < 3; ch++)
        {
            int low = 255, high = 0;
            for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
            {
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    int v = theirs[(ny * width + nx) * 3 + ch];
                    (low, high) = (Math.Min(low, v), Math.Max(high, v));
                }
            }
            int value = ours[i * 3 + ch];
            if (value < low - EdgeSlack || value > high + EdgeSlack) return true;
        }
        return false;
    }

    private static string Wrap(string body, string root) => $"<svg xmlns=\"http://www.w3.org/2000/svg\" {root}>{body}</svg>";

    private static Dictionary<string, (int, int, byte[])> RenderAll()
    {
        const int gap = 4;
        var tiles = new List<(string Name, int Y, int Width, int Height)>();
        var html = new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#fff\">");
        int y = 0, pageWidth = 1;
        foreach (var row in Documents)
        {
            (string name, string root, string body) = row.Data;
            string svg = Wrap(body, root);
            SvgDocument document = SvgReader.Parse(svg);
            int width = (int)MathF.Round(document.Width), height = (int)MathF.Round(document.Height);
            html.Append($"<img style=\"position:absolute;left:0;top:{y}px\" src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}\">");
            tiles.Add((name, y, width, height));
            y += height + gap;
            pageWidth = Math.Max(pageWidth, width + gap);
        }
        html.Append("</body></html>");

        byte[] page = Browser.Capture(html.ToString(), pageWidth, y);
        var rendered = new Dictionary<string, (int, int, byte[])>();
        foreach ((string name, int top, int width, int height) in tiles)
        {
            byte[] rgb = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++)
                {
                    int from = ((top + row) * pageWidth + x) * 4;
                    page.AsSpan(from, 3).CopyTo(rgb.AsSpan((row * width + x) * 3));
                }
            }
            rendered[name] = (width, height, rgb);
        }
        return rendered;
    }

    /// <summary>Finds a Chromium-family browser and has it capture a page, headless. RGBA bytes, <c>width</c> wide.</summary>
    private static class Browser
    {
        public const string Variable = "SVG_BROWSER";

        public static readonly string? Path = Resolve();

        public static byte[] Capture(string html, int width, int height)
        {
            string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpastro-fonts-svg-oracle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string page = System.IO.Path.Combine(work, "page.html"), shot = System.IO.Path.Combine(work, "page.png");
                File.WriteAllText(page, html);
                var start = new ProcessStartInfo(Path!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string argument in (string[])
                [
                    "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars", "--no-first-run",
                    "--disable-extensions", "--disable-background-networking",
                    "--force-device-scale-factor=1", "--force-color-profile=srgb",
                    $"--user-data-dir={System.IO.Path.Combine(work, "profile")}",
                    $"--window-size={width},{height}",
                    $"--screenshot={shot}",
                    new Uri(page).AbsoluteUri,
                ])
                {
                    start.ArgumentList.Add(argument);
                }

                // The capture is done when the browser says so on stderr, not when it exits: now and then a browser
                // writes the whole screenshot and never exits. Wait for the message or the exit, give a finished
                // browser a moment to leave, and end it if it does not. Everything waits on an event or a timer.
                var log = new StringBuilder();
                var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (log) log.AppendLine(e.Data);
                    if (e.Data.Contains("written to file", StringComparison.Ordinal)) written.TrySetResult();
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    Task exited = process.WaitForExitAsync(TestContext.Current.CancellationToken);
                    Task.WhenAny(exited, written.Task, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)).GetAwaiter().GetResult();
                    if (written.Task.IsCompleted) exited.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                    if (!(exited.IsCompleted || written.Task.IsCompleted) || !File.Exists(shot))
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
                return rgba!;
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

        private static string? Resolve()
        {
            if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named) return File.Exists(named) ? named : null;

            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (string? root in (string?[])
                    [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData")])
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    candidates.Add(System.IO.Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                    candidates.Add(System.IO.Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
                candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            }
            else
            {
                foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (string name in (string[])["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge"])
                    {
                        candidates.Add(System.IO.Path.Combine(directory, name));
                    }
                }
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
