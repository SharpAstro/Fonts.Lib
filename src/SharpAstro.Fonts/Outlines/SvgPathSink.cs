using System.Globalization;
using System.Text;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Captures glyph path commands as an SVG <c>path</c> "d" attribute string.
/// Useful for eyeballing outline-parser correctness before the rasterizer
/// lands. One sink per call; not thread-safe (instance state).
/// </summary>
public sealed class SvgPathSink : IGlyphSink
{
    private readonly StringBuilder _sb = new();

    public string PathData => _sb.ToString();

    public void MoveTo(float x, float y)  => Append('M', x, y);
    public void LineTo(float x, float y)  => Append('L', x, y);
    public void QuadTo(float cx, float cy, float x, float y)
    {
        if (_sb.Length > 0) _sb.Append(' ');
        _sb.Append('Q');
        AppendCoord(cx); _sb.Append(' '); AppendCoord(cy);
        _sb.Append(' '); AppendCoord(x); _sb.Append(' '); AppendCoord(y);
    }
    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        if (_sb.Length > 0) _sb.Append(' ');
        _sb.Append('C');
        AppendCoord(c1x); _sb.Append(' '); AppendCoord(c1y);
        _sb.Append(' '); AppendCoord(c2x); _sb.Append(' '); AppendCoord(c2y);
        _sb.Append(' '); AppendCoord(x); _sb.Append(' '); AppendCoord(y);
    }
    public void Close() => _sb.Append(" Z");

    private void Append(char cmd, float x, float y)
    {
        if (_sb.Length > 0) _sb.Append(' ');
        _sb.Append(cmd);
        AppendCoord(x); _sb.Append(' '); AppendCoord(y);
    }

    private void AppendCoord(float v)
        => _sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
}
