namespace SharpAstro.Fonts.Outlines;

/// <summary>Translate every coordinate by (dx, dy): how an accented composite (Type 1 <c>seac</c>, a Type 2
/// <c>endchar</c> with four operands) places its accent over its base.</summary>
internal sealed class TranslatingGlyphSink(IGlyphSink inner, float dx, float dy) : IGlyphSink
{
    public void MoveTo(float x, float y) => inner.MoveTo(x + dx, y + dy);
    public void LineTo(float x, float y) => inner.LineTo(x + dx, y + dy);
    public void QuadTo(float cx, float cy, float x, float y)
        => inner.QuadTo(cx + dx, cy + dy, x + dx, y + dy);
    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        => inner.CubicTo(c1x + dx, c1y + dy, c2x + dx, c2y + dy, x + dx, y + dy);
    public void Close() => inner.Close();
}
