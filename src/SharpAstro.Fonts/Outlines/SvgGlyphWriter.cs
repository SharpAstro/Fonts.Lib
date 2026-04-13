using System.Globalization;
using System.Text;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Wraps an <see cref="Outline"/> in a self-contained SVG document. Font
/// coordinates are y-up; SVG is y-down, so we apply a Y-flip transform via
/// the viewBox so paths render right-side-up.
/// </summary>
public static class SvgGlyphWriter
{
    /// <summary>
    /// Serialize an outline as an SVG document. <paramref name="title"/> is
    /// embedded as a <c>&lt;title&gt;</c> for hover tooltips. The fill is
    /// solid black with even-odd fill (matches TrueType winding semantics).
    /// </summary>
    public static string ToSvg(Outline outline, string title = "")
    {
        var sink = new SvgPathSink();
        BezierFlattener.Walk(outline, sink);

        var (xMin, yMin, xMax, yMax) = outline.Bounds;
        // If the recorded bbox is degenerate (some fonts ship 0,0,0,0 for
        // empty/dummy glyphs), fall back to a unit box so the SVG is valid.
        if (xMax <= xMin) { xMin = 0; xMax = 1; }
        if (yMax <= yMin) { yMin = 0; yMax = 1; }

        var width = xMax - xMin;
        var height = yMax - yMin;

        var sb = new StringBuilder(512);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append(CultureInfo.InvariantCulture, $"viewBox=\"{xMin} {-yMax} {width} {height}\" ");
        sb.Append(CultureInfo.InvariantCulture, $"width=\"{width}\" height=\"{height}\">\n");
        if (!string.IsNullOrEmpty(title))
            sb.Append("  <title>").Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title>\n");
        // Y-flip so font's y-up renders correctly in SVG's y-down space.
        sb.Append("  <g transform=\"scale(1,-1)\">\n");
        sb.Append("    <path fill=\"black\" fill-rule=\"evenodd\" d=\"")
          .Append(sink.PathData)
          .Append("\"/>\n");
        sb.Append("  </g>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
