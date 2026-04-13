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
    /// Serialize a TrueType outline as an SVG document. <paramref name="title"/> is
    /// embedded as a <c>&lt;title&gt;</c> for hover tooltips. The fill is
    /// solid black with even-odd fill (matches TrueType winding semantics).
    /// </summary>
    public static string ToSvg(Outline outline, string title = "")
    {
        var sink = new SvgPathSink();
        BezierFlattener.Walk(outline, sink);

        float xMin = outline.Bounds.XMin, yMin = outline.Bounds.YMin;
        float xMax = outline.Bounds.XMax, yMax = outline.Bounds.YMax;
        if (xMax <= xMin) { xMin = 0; xMax = 1; }
        if (yMax <= yMin) { yMin = 0; yMax = 1; }
        return Wrap(sink.PathData, xMin, yMin, xMax, yMax, title);
    }

    /// <summary>
    /// Serialize a glyph from any source (TrueType or CFF) as an SVG document.
    /// Bounds are computed by tracking the bbox of all path points and
    /// control points emitted to the sink.
    /// </summary>
    public static string ToSvg(Action<IGlyphSink> drawTo, string title = "")
    {
        var pathSink = new SvgPathSink();
        var boundsSink = new BoundsSink(pathSink);
        drawTo(boundsSink);

        float xMin = boundsSink.MinX, yMin = boundsSink.MinY;
        float xMax = boundsSink.MaxX, yMax = boundsSink.MaxY;
        if (xMax <= xMin) { xMin = 0; xMax = 1; }
        if (yMax <= yMin) { yMin = 0; yMax = 1; }
        return Wrap(pathSink.PathData, xMin, yMin, xMax, yMax, title);
    }

    private static string Wrap(string pathData, float xMin, float yMin,
        float xMax, float yMax, string title)
    {

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
          .Append(pathData)
          .Append("\"/>\n");
        sb.Append("  </g>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}

/// <summary>
/// Wraps another sink and tracks the bbox of every endpoint and control
/// point seen. Useful when we need viewBox bounds for a CFF glyph that has
/// no precomputed Outline.Bounds.
/// </summary>
internal sealed class BoundsSink : IGlyphSink
{
    private readonly IGlyphSink _inner;
    public float MinX { get; private set; } = float.PositiveInfinity;
    public float MinY { get; private set; } = float.PositiveInfinity;
    public float MaxX { get; private set; } = float.NegativeInfinity;
    public float MaxY { get; private set; } = float.NegativeInfinity;

    public BoundsSink(IGlyphSink inner) => _inner = inner;

    private void Track(float x, float y)
    {
        if (x < MinX) MinX = x;
        if (x > MaxX) MaxX = x;
        if (y < MinY) MinY = y;
        if (y > MaxY) MaxY = y;
    }

    public void MoveTo(float x, float y) { Track(x, y); _inner.MoveTo(x, y); }
    public void LineTo(float x, float y) { Track(x, y); _inner.LineTo(x, y); }
    public void QuadTo(float cx, float cy, float x, float y)
    { Track(cx, cy); Track(x, y); _inner.QuadTo(cx, cy, x, y); }
    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    { Track(c1x, c1y); Track(c2x, c2y); Track(x, y); _inner.CubicTo(c1x, c1y, c2x, c2y, x, y); }
    public void Close() => _inner.Close();
}
