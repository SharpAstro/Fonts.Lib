using System.Text;
using BenchmarkDotNet.Attributes;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Benchmarks;

/// <summary>
/// Drawing a vector scene to pixels, whole, through <see cref="SceneRasterizer"/>: what it costs to turn an SVG that has
/// already been read into something to put on screen.
/// </summary>
/// <remarks>
/// <para>A scene is rasterised once a size and the result kept, so these are costs paid at start-up or when a size is
/// first asked for, never in a frame. Four shapes of work:</para>
/// <list type="bullet">
/// <item><b>Outline icon, coverage at 20 px</b>: what baking one mark to a mask costs. Three stroked paths.</item>
/// <item><b>Outline icon, coverage at 64 px</b>: the same at a size a high-DPI toolbar asks for.</item>
/// <item><b>Colour drawing, RGBA at 256 px</b>: gradients, strokes and a layer.</item>
/// <item><b>Many shapes, RGBA at 512 px</b>: 1,000 filled curves, where the per-shape cost shows.</item>
/// </list>
/// <para>The scenes are read in setup; reading is measured in the Codecs repo (<c>SvgReadBenchmarks</c>). Run with
/// <c>--filter '*SceneRasterizer*' --job short</c> and only compare cases from one run.</para>
/// </remarks>
[MemoryDiagnoser]
public class SceneRasterizerBenchmarks
{
    private VectorScene _icon = null!, _colour = null!, _many = null!;

    [GlobalSetup]
    public void Setup()
    {
        _icon = Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<path d=\"M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z\"/><path d=\"M12 6v6l4 2\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>", 3);

        _colour = Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\"><defs><radialGradient id=\"s\" fx=\"0.35\" fy=\"0.3\"><stop offset=\"0\" stop-color=\"#fff7b0\"/><stop offset=\"1\" stop-color=\"#f5a500\"/></radialGradient>" +
            "<linearGradient id=\"c\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\"/><stop offset=\"1\" stop-color=\"#b9c7d6\"/></linearGradient></defs>" +
            "<circle cx=\"24\" cy=\"24\" r=\"14\" fill=\"url(#s)\"/><g opacity=\"0.9\"><g stroke=\"#f5a500\" stroke-width=\"3\" stroke-linecap=\"round\"><path d=\"M24 3v5M3 24h5M9 9l4 4M39 9l-4 4\"/></g></g>" +
            "<path d=\"M20 52a10 10 0 0 1 2-19.8A14 14 0 0 1 49 34a9 9 0 0 1 1 18z\" fill=\"url(#c)\" stroke=\"#8fa3b8\" stroke-width=\"1.5\"/></svg>", 3);

        var many = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"512\" height=\"512\">");
        var random = new Random(1);
        for (int i = 0; i < 1000; i++)
        {
            many.Append("<path fill=\"#").Append(random.Next(0x1000000).ToString("x6", System.Globalization.CultureInfo.InvariantCulture)).Append("\" d=\"M").Append(random.Next(480)).Append(' ').Append(random.Next(480));
            for (int k = 0; k < 6; k++)
            {
                many.Append(" c").Append(random.Next(-16, 16)).Append(' ').Append(random.Next(-16, 16)).Append(' ')
                    .Append(random.Next(-16, 16)).Append(' ').Append(random.Next(-16, 16)).Append(' ')
                    .Append(random.Next(-16, 16)).Append(' ').Append(random.Next(-16, 16));
            }
            many.Append("z\"/>");
        }
        _many = Read(many.Append("</svg>").ToString(), 1000);
    }

    // A benchmark of a scene that turned out empty would measure nothing: say what each one holds.
    private static VectorScene Read(string svg, int nodes)
    {
        SvgDocument document = SvgReader.Parse(svg);
        if (document.Scene.Nodes.Length != nodes || document.Issues.Length != 0)
        {
            throw new InvalidOperationException($"Expected {nodes} nodes and no issues, read {document.Scene.Nodes.Length} and {document.Issues.Length}.");
        }
        return document.Scene;
    }

    [Benchmark(Description = "outline icon, coverage 20 px")]
    public int IconCoverage20() => SceneRasterizer.RasterizeCoverage(_icon, 20, 20).Length;

    [Benchmark(Description = "outline icon, coverage 64 px")]
    public int IconCoverage64() => SceneRasterizer.RasterizeCoverage(_icon, 64, 64).Length;

    [Benchmark(Description = "colour drawing, RGBA 256 px")]
    public int Colour256() => SceneRasterizer.Rasterize(_colour, 256, 256).Pixels.Length;

    [Benchmark(Description = "1,000 shapes, RGBA 512 px")]
    public int Many512() => SceneRasterizer.Rasterize(_many, 512, 512).Pixels.Length;
}
