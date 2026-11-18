using System.Collections.Immutable;
using System.Numerics;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Color;

/// <summary>
/// Draws the glyphs of an OpenType 'SVG ' table: the document is read by <c>SharpAstro.Svg</c> into a vector scene and
/// the scene is drawn by <see cref="SceneRasterizer"/>, the same two steps an SVG file takes.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/svg
///
/// <para>What the specification asks of whoever draws the glyph is given to the reader as options, and nothing about
/// fonts is known to it:</para>
/// <list type="bullet">
/// <item>the glyph is the element with the id <c>glyph&lt;id&gt;</c>, drawn as a <c>use</c> of it standing alone in the
/// root (<see cref="SvgReadOptions.ElementId"/>);</item>
/// <item>the viewport is the em square, one unit a font design unit, and it does not clip
/// (<see cref="SvgReadOptions.ViewportWidth"/>; the reader never clips to the root);</item>
/// <item><c>currentColor</c> is the text colour (<see cref="SvgReadOptions.CurrentColor"/>);</item>
/// <item>the entries of a 'CPAL' palette are the custom properties <c>--color0</c>, <c>--color1</c> and so on, which
/// a glyph reads with <c>var()</c> (<see cref="SvgReadOptions.CustomProperties"/>). An entry's alpha goes with it.</item>
/// </list>
///
/// <para>A document is read again for every glyph drawn from it, and a document may hold many glyphs. Callers keep
/// what they render (a glyph atlas does), so this is the cost of the first drawing of each glyph, not of each frame.</para>
/// </summary>
public static class SvgGlyphRenderer
{
    /// <summary>The name of the reader's issue for an id no element has. The reader's own tests pin the text.</summary>
    private const string NoSuchElement = "no element with the id asked for";

    /// <summary>
    /// The glyph's SVG description as a scene, in font design units with y pointing DOWN and the origin at the glyph's
    /// origin on the baseline: the specification's coordinates, so what is above the baseline has a negative y.
    /// Null when the font has no SVG description of the glyph, or its document cannot be read.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="glyphId">The glyph.</param>
    /// <param name="foreground">What <c>currentColor</c> is: the colour of the text. Black when not given.</param>
    /// <param name="paletteIndex">Which 'CPAL' palette the glyph's <c>var(--colorN)</c> read. A font without one, or an index it does not have, leaves them to their fallbacks.</param>
    public static VectorScene? TryReadScene(OpenTypeFont font, uint glyphId, Rgba32? foreground = null, int paletteIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.Svg is not { } table || !table.TryGetDocument(glyphId, out ReadOnlyMemory<byte> document)) return null;

        Rgba32 text = foreground ?? Rgba32.Black;
        var options = new SvgReadOptions
        {
            ElementId = "glyph" + glyphId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ViewportWidth = font.UnitsPerEm,
            ViewportHeight = font.UnitsPerEm,
            CurrentColor = new SharpAstro.VectorGraphics.Rgba32(text.R, text.G, text.B, text.A),
            CustomProperties = PaletteProperties(font, paletteIndex),
        };
        try
        {
            SvgDocument read = SvgReader.Read(document, options);
            if (read.Scene.Nodes.IsEmpty) return null;
            foreach (SvgIssue issue in read.Issues)
            {
                if (issue.Kind == SvgIssueKind.Invalid && issue.What == NoSuchElement) return null;
            }
            return read.Scene;
        }
        catch (InvalidDataException)
        {
            // Not XML, not SVG, or over one of the reader's limits: the glyph has no usable SVG description, and its
            // outline is what is left to draw.
            return null;
        }
    }

    /// <summary>
    /// Renders the glyph's SVG description at <paramref name="pixelsPerEm"/>. Null when the font has none for this
    /// glyph or its document cannot be read (the caller falls back to the outline); an empty bitmap when it has one that
    /// leaves no pixel.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="glyphId">The glyph.</param>
    /// <param name="pixelsPerEm">The size.</param>
    /// <param name="foreground">What <c>currentColor</c> is: the colour of the text. Black when not given.</param>
    /// <param name="paletteIndex">Which 'CPAL' palette the glyph's <c>var(--colorN)</c> read.</param>
    /// <param name="decodeImage">Decodes pictures embedded in the glyph; null for <see cref="SceneRasterizer.DecodePng"/>, which reads PNG alone.</param>
    public static ColorBitmap? TryRender(OpenTypeFont font, uint glyphId, float pixelsPerEm,
        Rgba32? foreground = null, int paletteIndex = 0, SceneImageDecoder? decodeImage = null)
    {
        if (!(pixelsPerEm > 0) || !float.IsFinite(pixelsPerEm)) return null;
        if (TryReadScene(font, glyphId, foreground, paletteIndex) is not { } scene) return null;

        float scale = pixelsPerEm / font.UnitsPerEm;
        if (!TryBounds(scene.Nodes, Matrix3x2.CreateScale(scale), out Vector2 min, out Vector2 max)) return ColorBitmap.Empty;

        // What is drawn, in whole pixels, and no further than a few ems from the origin: the bounds are the document's
        // word (a glyph a thousand ems wide is an allocation, not a glyph).
        float reach = MathF.Ceiling(pixelsPerEm) * 3 + 2;
        int left = (int)MathF.Floor(MathF.Max(min.X, -reach)) - 1, top = (int)MathF.Floor(MathF.Max(min.Y, -reach)) - 1;
        int right = (int)MathF.Ceiling(MathF.Min(max.X, reach)) + 1, bottom = (int)MathF.Ceiling(MathF.Min(max.Y, reach)) + 1;
        if (right <= left || bottom <= top) return ColorBitmap.Empty;

        var toPixels = new Matrix3x2(scale, 0, 0, scale, -left, -top);
        ColorBitmap drawn = SceneRasterizer.Rasterize(scene, right - left, bottom - top, toPixels, decodeImage);
        return Crop(drawn, left, top);
    }

    /// <summary>The palette as custom properties: <c>--color0</c> and on, each <c>#rrggbbaa</c>. Null when there is no such palette.</summary>
    private static Dictionary<string, string>? PaletteProperties(OpenTypeFont font, int paletteIndex)
    {
        if (font.Cpal is not { } cpal) return null;
        ReadOnlySpan<Rgba32> palette = cpal.GetPalette(paletteIndex);
        if (palette.IsEmpty) return null;
        var properties = new Dictionary<string, string>(palette.Length, StringComparer.Ordinal);
        for (int i = 0; i < palette.Length; i++)
        {
            Rgba32 c = palette[i];
            properties[string.Create(System.Globalization.CultureInfo.InvariantCulture, $"--color{i}")] = $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";
        }
        return properties;
    }

    /// <summary>
    /// A box that holds everything the nodes draw, once <paramref name="toPixels"/> is applied. Generous, not tight: a
    /// stroke is taken at the most its joins and caps can reach, and a clip or mask is not looked at. False when
    /// nothing is drawn.
    /// </summary>
    private static bool TryBounds(ImmutableArray<VectorNode> nodes, Matrix3x2 toPixels, out Vector2 min, out Vector2 max)
    {
        var low = new Vector2(float.PositiveInfinity);
        var high = new Vector2(float.NegativeInfinity);
        Add(nodes);
        (min, max) = (low, high);
        return low.X <= high.X && low.Y <= high.Y && float.IsFinite(low.X + low.Y + high.X + high.Y);

        void Add(ImmutableArray<VectorNode> list)
        {
            foreach (VectorNode node in list)
            {
                switch (node)
                {
                    case VectorGroup group:
                        Add(group.Children);
                        break;
                    case VectorImage image:
                        Box(image.Bounds, 0, image.Transform * toPixels);
                        break;
                    case VectorShape shape:
                        if (!shape.Path.TryGetBounds(out VectorRect box)) break;
                        float grow = 0;
                        if (shape.Stroke is { } stroke)
                        {
                            // Half the width, times how far a miter or a square cap can stand out from it.
                            float most = stroke.Join == LineJoin.Miter ? MathF.Max(1, stroke.MiterLimit) : 1;
                            if (stroke.Cap == LineCap.Square) most = MathF.Max(most, 1.4143f);
                            grow = stroke.Width / 2 * most;
                        }
                        Box(box, grow, shape.Transform * toPixels);
                        break;
                }
            }
        }

        void Box(VectorRect box, float grow, Matrix3x2 map)
        {
            ReadOnlySpan<Vector2> corners =
            [
                new(box.X - grow, box.Y - grow), new(box.Right + grow, box.Y - grow),
                new(box.X - grow, box.Bottom + grow), new(box.Right + grow, box.Bottom + grow),
            ];
            foreach (Vector2 corner in corners)
            {
                Vector2 p = Vector2.Transform(corner, map);
                low = Vector2.Min(low, p);
                high = Vector2.Max(high, p);
            }
        }
    }

    /// <summary>
    /// The drawn canvas cut down to its pixels that are not transparent, as a glyph bitmap: <c>Left</c> is where its
    /// left edge is from the pen, <c>Top</c> how far its top row is above the baseline. The canvas's own corner is at
    /// (<paramref name="left"/>, <paramref name="top"/>) in pixels from the glyph's origin, y down.
    /// </summary>
    private static ColorBitmap Crop(ColorBitmap canvas, int left, int top)
    {
        int x0 = canvas.Width, y0 = canvas.Height, x1 = 0, y1 = 0;
        ReadOnlySpan<byte> pixels = canvas.Pixels;
        for (int y = 0; y < canvas.Height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * canvas.Width * 4, canvas.Width * 4);
            for (int x = 0; x < canvas.Width; x++)
            {
                if (row[x * 4 + 3] == 0) continue;
                if (x < x0) x0 = x;
                if (x + 1 > x1) x1 = x + 1;
                if (y < y0) y0 = y;
                y1 = y + 1;
            }
        }
        if (x0 >= x1 || y0 >= y1) return ColorBitmap.Empty;

        int width = x1 - x0, height = y1 - y0;
        byte[] cut = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            pixels.Slice(((y0 + y) * canvas.Width + x0) * 4, width * 4).CopyTo(cut.AsSpan(y * width * 4));
        }
        return new ColorBitmap(cut, width, height, left + x0, -(top + y0));
    }
}
