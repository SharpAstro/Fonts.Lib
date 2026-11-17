using System.Collections.Immutable;
using System.Numerics;
using SharpAstro.Fonts.Color;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Png;
using SharpAstro.VectorGraphics;
using FontLineCap = SharpAstro.Fonts.Rasterizer.LineCap;
using FontLineJoin = SharpAstro.Fonts.Rasterizer.LineJoin;
using SceneColor = SharpAstro.VectorGraphics.Rgba32;
using SceneLineCap = SharpAstro.VectorGraphics.LineCap;
using SceneLineJoin = SharpAstro.VectorGraphics.LineJoin;

namespace SharpAstro.Fonts.Scenes;

/// <summary>
/// Turns the picture a <see cref="VectorImage"/> carries into pixels (RGBA, not premultiplied), or returns null when
/// it cannot. A scene holds a picture as the file it came as, or only as an address; what is decoded, and whether
/// anything is fetched, is the caller's decision, made here.
/// </summary>
public delegate ColorBitmap? SceneImageDecoder(VectorImage image);

/// <summary>
/// Draws a <see cref="VectorScene"/> (what <c>SharpAstro.Svg</c> reads an SVG document into) to pixels.
/// </summary>
/// <remarks>
/// <para><b>Nothing here is a second rasteriser.</b> Coverage comes from <see cref="SmoothRasterizer"/> (the same edges,
/// sub-scanlines and span arithmetic a glyph gets, through its device-space entry), strokes from
/// <see cref="OutlineStroker"/>, and gradients are projected by the functions <see cref="ColrRenderer"/> uses for COLR
/// glyphs. This type only walks the scene and composites.</para>
/// <para><b>Compositing</b> is source-over in premultiplied floats, in the sRGB values as written (no linearisation),
/// which is what a browser does for SVG. A <see cref="VectorGroup"/> is drawn into a layer of its own, cut to its clip,
/// multiplied by its mask, and blended at its opacity. A shape's fill is drawn before its stroke.</para>
/// <para><b>Clips</b> are coverage, not geometry: each outline is rasterised like a fill and the outlines of one clip
/// are added up (and cut off at 1), which is exact where they lie side by side or one inside another and a little
/// heavy only where two edges fall in the same pixel. <b>Patterns</b> are drawn once into a tile of pixels at the size
/// they have on the canvas and that tile is repeated, sampled between its four nearest pixels, which is what a browser
/// does and why a turned pattern's edges are a little soft. <b>Images</b> are sampled the same way; one much larger
/// than the place it is drawn in is not filtered down, so give it a picture of about the right size.</para>
/// <para><b>Pictures.</b> Without a <see cref="SceneImageDecoder"/> an embedded PNG is decoded and every other
/// picture (another format, or one the scene only names) is not drawn. Nothing here fetches anything.</para>
/// <para><b>Not for every frame.</b> A call allocates its canvas and scratch. Rasterise a scene once per size and keep
/// the result.</para>
/// <para>Stateless and safe to call from any thread.</para>
/// </remarks>
public static class SceneRasterizer
{
    /// <summary>The scene scaled to fill <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
    /// <returns>RGBA, not premultiplied, transparent where nothing was drawn; <c>Left</c> and <c>Top</c> are 0.</returns>
    public static ColorBitmap Rasterize(VectorScene scene, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Rasterize(scene, width, height, FitTransform(scene, width, height));
    }

    /// <summary>The scene drawn through <paramref name="sceneToPixels"/> onto a canvas of the given size.</summary>
    /// <returns>RGBA, not premultiplied, transparent where nothing was drawn; <c>Left</c> and <c>Top</c> are 0.</returns>
    public static ColorBitmap Rasterize(VectorScene scene, int width, int height, Matrix3x2 sceneToPixels) =>
        Rasterize(scene, width, height, sceneToPixels, null);

    /// <summary>The scene drawn through <paramref name="sceneToPixels"/> onto a canvas of the given size, its pictures decoded by <paramref name="decodeImage"/>.</summary>
    /// <param name="decodeImage">
    /// Decodes the scene's pictures; null for <see cref="DecodePng"/>, which reads an embedded PNG and nothing else. A
    /// decoder for more formats can fall back to it. A picture it returns null for is not drawn.
    /// </param>
    /// <returns>RGBA, not premultiplied, transparent where nothing was drawn; <c>Left</c> and <c>Top</c> are 0.</returns>
    public static ColorBitmap Rasterize(VectorScene scene, int width, int height, Matrix3x2 sceneToPixels, SceneImageDecoder? decodeImage)
    {
        float[] layer = Draw(scene, width, height, sceneToPixels, decodeImage);
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < layer.Length; i += 4)
        {
            float a = layer[i + 3];
            if (a <= 0) continue;
            float scale = 255f / a;
            pixels[i] = ToByte(layer[i] * scale);
            pixels[i + 1] = ToByte(layer[i + 1] * scale);
            pixels[i + 2] = ToByte(layer[i + 2] * scale);
            pixels[i + 3] = ToByte(a * 255f);
        }
        return new ColorBitmap(pixels, width, height, left: 0, top: 0);
    }

    /// <summary>
    /// Only how much of each pixel the scene covers: the alpha of <see cref="Rasterize(VectorScene, int, int)"/>,
    /// one byte a pixel. What a single-colour mark needs, since its colour comes from whoever draws it.
    /// </summary>
    public static byte[] RasterizeCoverage(VectorScene scene, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return RasterizeCoverage(scene, width, height, FitTransform(scene, width, height));
    }

    /// <inheritdoc cref="RasterizeCoverage(VectorScene, int, int)"/>
    public static byte[] RasterizeCoverage(VectorScene scene, int width, int height, Matrix3x2 sceneToPixels) =>
        RasterizeCoverage(scene, width, height, sceneToPixels, null);

    /// <inheritdoc cref="RasterizeCoverage(VectorScene, int, int)"/>
    /// <param name="decodeImage">As for <see cref="Rasterize(VectorScene, int, int, Matrix3x2, SceneImageDecoder?)"/>.</param>
    public static byte[] RasterizeCoverage(VectorScene scene, int width, int height, Matrix3x2 sceneToPixels, SceneImageDecoder? decodeImage)
    {
        float[] layer = Draw(scene, width, height, sceneToPixels, decodeImage);
        byte[] alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = ToByte(layer[i * 4 + 3] * 255f);
        return alpha;
    }

    private static Matrix3x2 FitTransform(VectorScene scene, int width, int height) =>
        scene.Width > 0 && scene.Height > 0 ? Matrix3x2.CreateScale(width / scene.Width, height / scene.Height) : Matrix3x2.Identity;

    private static byte ToByte(float v) => (byte)Math.Clamp(v + 0.5f, 0f, 255f);

    /// <summary>
    /// The decoder used when none is given: an embedded PNG (by its signature, whatever media type the scene has for
    /// it) as pixels, and null for everything else.
    /// </summary>
    public static ColorBitmap? DecodePng(VectorImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReadOnlySpan<byte> data = image.Data.Span;
        if (data.Length < 8 || !data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return null;
        return PngImageDecoder.TryDecodeRgba8(data, out byte[]? pixels, out int w, out int h) && pixels is not null
            ? new ColorBitmap(pixels, w, h, left: 0, top: 0)
            : null;
    }

    /// <summary>The largest side, in pixels, a pattern's tile is drawn at. A larger one is drawn at this and stretched.</summary>
    private const int MaxTileSide = 1024;

    private static float[] Draw(VectorScene scene, int width, int height, Matrix3x2 sceneToPixels, SceneImageDecoder? decodeImage)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        // A float layer is 16 bytes a pixel, and a group needs another: keep one canvas well inside an array.
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)width * height, 1L << 26, "width * height");

        var canvas = new Canvas(width, height, sceneToPixels, new Shared(decodeImage ?? DecodePng));
        float[] layer = new float[width * height * 4];
        canvas.Draw(scene.Nodes, layer);
        return layer;
    }

    /// <summary>What one call's canvases share: the decoder, and each picture and pattern tile once it has been made.</summary>
    private sealed class Shared(SceneImageDecoder decode)
    {
        private readonly Dictionary<VectorImage, ColorBitmap?> _pictures = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<(PatternPaint Pattern, int Width, int Height), Tile> Tiles = [];

        public ColorBitmap? Picture(VectorImage image)
        {
            if (!_pictures.TryGetValue(image, out ColorBitmap? picture))
            {
                picture = decode(image);
                if (picture is not null && (picture.IsEmpty || picture.Pixels.Length < (long)picture.Width * picture.Height * 4)) picture = null;
                _pictures[image] = picture;
            }
            return picture;
        }
    }

    /// <summary>A pattern's tile as pixels (premultiplied RGBA floats), repeated over the plane.</summary>
    private sealed class Tile(float[] pixels, int width, int height, VectorRect tile)
    {
        /// <summary>The colour at a point of pattern space: RGBA, not premultiplied.</summary>
        public Vector4 At(Vector2 q)
        {
            // Into the tile, in pixels, pixel centres at half-integers; then between the four nearest, wrapping round.
            float u = (q.X - tile.X) / tile.Width, v = (q.Y - tile.Y) / tile.Height;
            float fx = (u - MathF.Floor(u)) * width - 0.5f, fy = (v - MathF.Floor(v)) * height - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            Vector4 c = Vector4.Lerp(
                Vector4.Lerp(Pixel(x0, y0), Pixel(x0 + 1, y0), tx),
                Vector4.Lerp(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), tx), ty);
            return c.W > 0 ? new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W) : default;
        }

        private Vector4 Pixel(int x, int y)
        {
            x = (x % width + width) % width;
            y = (y % height + height) % height;
            int i = (y * width + x) * 4;
            return new Vector4(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        }
    }

    /// <summary>One canvas's state: its size, the scene transform, and the coverage scratch every shape reuses.</summary>
    private sealed class Canvas(int width, int height, Matrix3x2 sceneToPixels, Shared shared)
    {
        private readonly float[] _coverage = new float[width * height];

        public void Draw(ImmutableArray<VectorNode> nodes, float[] layer)
        {
            foreach (VectorNode node in nodes)
            {
                switch (node)
                {
                    case VectorGroup group:
                    {
                        // Its own layer, so that the children overlap each other at full strength and fade as one.
                        float[] inner = new float[layer.Length];
                        Draw(group.Children, inner);
                        // How much of the layer is kept, pixel by pixel: the clip's coverage times the mask's factor.
                        float[]? kept = group.Clip is { } clip ? Region(clip) : null;
                        if (group.Mask is { } mask)
                        {
                            float[] picture = new float[layer.Length];
                            Draw(mask.Children, picture);
                            kept ??= Ones();
                            for (int p = 0, i = 0; p < kept.Length; p++, i += 4)
                            {
                                // The picture is premultiplied, so its colour already carries its alpha.
                                kept[p] *= mask.Kind == VectorMaskKind.Alpha
                                    ? picture[i + 3]
                                    : 0.2125f * picture[i] + 0.7154f * picture[i + 1] + 0.0721f * picture[i + 2];
                            }
                        }
                        float opacity = Math.Clamp(group.Opacity, 0f, 1f);
                        for (int i = 0; i < layer.Length; i += 4)
                        {
                            float factor = kept is null ? opacity : opacity * Math.Clamp(kept[i >> 2], 0f, 1f);
                            float a = inner[i + 3] * factor;
                            if (a <= 0) continue;
                            float keep = 1f - a;
                            layer[i] = inner[i] * factor + layer[i] * keep;
                            layer[i + 1] = inner[i + 1] * factor + layer[i + 1] * keep;
                            layer[i + 2] = inner[i + 2] * factor + layer[i + 2] * keep;
                            layer[i + 3] = a + layer[i + 3] * keep;
                        }
                        break;
                    }
                    case VectorShape shape:
                        Draw(shape, layer);
                        break;
                    case VectorImage image:
                        Draw(image, layer);
                        break;
                    default:
                        throw new NotSupportedException($"A scene node of type {node.GetType().Name} is not one this rasteriser knows.");
                }
            }
        }

        private float[] Ones()
        {
            float[] ones = new float[width * height];
            Array.Fill(ones, 1f);
            return ones;
        }

        /// <summary>
        /// A clip as coverage, one float a pixel: its outlines added up and cut off at 1, each first cut by its own
        /// clip, and the whole cut by the clip it intersects.
        /// </summary>
        private float[] Region(VectorClip clip)
        {
            float[] region = new float[width * height];
            foreach (VectorClipShape shape in clip.Shapes)
            {
                // Before this outline's coverage, because working it out uses the same scratch.
                float[]? within = shape.Clip is { } inner ? Region(inner) : null;
                Matrix3x2 toPixels = shape.Transform * sceneToPixels;
                if (!SmoothRasterizer.CoverageInto(sink => Emit(shape.Path, toPixels, sink, closeOpen: true), width, height,
                        shape.Rule == FillRule.EvenOdd, _coverage, out int x0, out int y0, out int x1, out int y1))
                {
                    continue;
                }
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        float c = _coverage[row + x];
                        if (within is not null) c *= within[row + x];
                        region[row + x] = MathF.Min(1f, region[row + x] + c);
                    }
                }
            }
            if (clip.Intersect is { } intersect)
            {
                float[] other = Region(intersect);
                for (int i = 0; i < region.Length; i++) region[i] *= other[i];
            }
            return region;
        }

        /// <summary>A picture: decoded once a call, fitted into its box, sampled between its four nearest pixels.</summary>
        private void Draw(VectorImage image, float[] layer)
        {
            float opacity = Math.Clamp(image.Opacity, 0f, 1f);
            VectorRect box = image.Bounds;
            if (opacity <= 0 || !(box.Width > 0) || !(box.Height > 0) || shared.Picture(image) is not { } picture) return;

            float sx = box.Width / picture.Width, sy = box.Height / picture.Height;
            if (image.Fit != ImageFit.Stretch) sx = sy = image.Fit == ImageFit.Slice ? MathF.Max(sx, sy) : MathF.Min(sx, sy);
            float ox = box.X + image.AlignX * (box.Width - picture.Width * sx), oy = box.Y + image.AlignY * (box.Height - picture.Height * sy);
            // What is drawn is the picture where it lies inside the box.
            float left = MathF.Max(box.X, ox), top = MathF.Max(box.Y, oy);
            float right = MathF.Min(box.Right, ox + picture.Width * sx), bottom = MathF.Min(box.Bottom, oy + picture.Height * sy);
            if (!(right > left) || !(bottom > top)) return;

            Matrix3x2 toPixels = image.Transform * sceneToPixels;
            if (!Matrix3x2.Invert(toPixels, out Matrix3x2 toLocal)) return;
            if (!SmoothRasterizer.CoverageInto(sink =>
                    {
                        Vector2 a = Vector2.Transform(new Vector2(left, top), toPixels), b = Vector2.Transform(new Vector2(right, top), toPixels);
                        Vector2 c = Vector2.Transform(new Vector2(right, bottom), toPixels), d = Vector2.Transform(new Vector2(left, bottom), toPixels);
                        sink.MoveTo(a.X, a.Y);
                        sink.LineTo(b.X, b.Y);
                        sink.LineTo(c.X, c.Y);
                        sink.LineTo(d.X, d.Y);
                        sink.Close();
                    }, width, height, evenOdd: false, _coverage, out int x0, out int y0, out int x1, out int y1))
            {
                return;
            }

            byte[] pixels = picture.Pixels;
            int pw = picture.Width, ph = picture.Height;
            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    float coverage = _coverage[row + x];
                    if (coverage <= 0) continue;
                    Vector2 q = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), toLocal);
                    // The picture's pixel centres are at half-integers; at its edges the outermost pixel goes on.
                    float fx = Math.Clamp((q.X - ox) / sx - 0.5f, 0f, pw - 1), fy = Math.Clamp((q.Y - oy) / sy - 0.5f, 0f, ph - 1);
                    int px = (int)fx, py = (int)fy, px1 = Math.Min(px + 1, pw - 1), py1 = Math.Min(py + 1, ph - 1);
                    float tx = fx - px, ty = fy - py;
                    Vector4 colour = Vector4.Lerp(
                        Vector4.Lerp(Texel(px, py), Texel(px1, py), tx),
                        Vector4.Lerp(Texel(px, py1), Texel(px1, py1), tx), ty) * (opacity * coverage);
                    if (colour.W <= 0) continue;
                    int i = (row + x) * 4;
                    float keep = 1f - colour.W;
                    layer[i] = colour.X + layer[i] * keep;
                    layer[i + 1] = colour.Y + layer[i + 1] * keep;
                    layer[i + 2] = colour.Z + layer[i + 2] * keep;
                    layer[i + 3] = colour.W + layer[i + 3] * keep;
                }
            }

            // One pixel of the picture, premultiplied, so that a transparent pixel's colour does not bleed into its neighbours.
            Vector4 Texel(int tx, int ty)
            {
                int i = (ty * pw + tx) * 4;
                float a = pixels[i + 3] / 255f;
                return new Vector4(pixels[i] / 255f * a, pixels[i + 1] / 255f * a, pixels[i + 2] / 255f * a, a);
            }
        }

        /// <summary>
        /// A pattern's tile as pixels, at the size it has on this canvas. Drawn once for a pattern and a size, by a
        /// canvas of its own: what the tile's content draws outside the tile falls off that canvas, which is the clip
        /// to the tile.
        /// </summary>
        private Tile? TileOf(PatternPaint pattern, Matrix3x2 patternToPixels)
        {
            VectorRect tile = pattern.Tile;
            if (!(tile.Width > 0) || !(tile.Height > 0) || pattern.Children.IsDefaultOrEmpty) return null;
            // How long the tile's sides are on the canvas.
            float scaleX = new Vector2(patternToPixels.M11, patternToPixels.M12).Length(), scaleY = new Vector2(patternToPixels.M21, patternToPixels.M22).Length();
            int w = (int)Math.Clamp(MathF.Ceiling(tile.Width * scaleX), 1f, MaxTileSide), h = (int)Math.Clamp(MathF.Ceiling(tile.Height * scaleY), 1f, MaxTileSide);
            if (shared.Tiles.TryGetValue((pattern, w, h), out Tile? made)) return made;

            Matrix3x2 toTile = Matrix3x2.CreateTranslation(-tile.X, -tile.Y) * Matrix3x2.CreateScale(w / tile.Width, h / tile.Height);
            float[] pixels = new float[w * h * 4];
            new Canvas(w, h, toTile, shared).Draw(pattern.Children, pixels);
            return shared.Tiles[(pattern, w, h)] = new Tile(pixels, w, h, tile);
        }

        private void Draw(VectorShape shape, float[] layer)
        {
            Matrix3x2 toPixels = shape.Transform * sceneToPixels;
            if (shape.Fill is { } fill)
            {
                if (SmoothRasterizer.CoverageInto(sink => Emit(shape.Path, toPixels, sink, closeOpen: true), width, height,
                        fill.Rule == FillRule.EvenOdd, _coverage, out int x0, out int y0, out int x1, out int y1))
                {
                    Paint(layer, x0, y0, x1, y1, fill.Paint, fill.Opacity, toPixels);
                }
            }
            if (shape.Stroke is { } stroke)
            {
                // A stroke outline overlaps itself at every join, so it is always filled non-zero.
                if (SmoothRasterizer.CoverageInto(sink => StrokeInto(shape.Path, stroke, toPixels, sink), width, height,
                        evenOdd: false, _coverage, out int x0, out int y0, out int x1, out int y1))
                {
                    Paint(layer, x0, y0, x1, y1, stroke.Paint, stroke.Opacity, toPixels);
                }
            }
        }

        /// <summary>Blends a paint over the layer wherever the coverage scratch says the shape is.</summary>
        private void Paint(float[] layer, int x0, int y0, int x1, int y1, VectorPaint paint, float opacity, Matrix3x2 toPixels)
        {
            opacity = Math.Clamp(opacity, 0f, 1f);
            if (opacity <= 0) return;

            Func<Vector2, Vector4>? colourAt = null;
            Vector4 solid = default;
            switch (paint)
            {
                case SolidPaint s:
                    solid = ToVector(s.Color);
                    break;
                case LinearGradientPaint linear:
                {
                    // Gradient space -> the shape's coordinates -> pixels; a pixel goes back the other way.
                    if (!Matrix3x2.Invert(linear.Transform * toPixels, out Matrix3x2 toGradient)) return;
                    Vector2 p0 = new(linear.X1, linear.Y1), p1 = new(linear.X2, linear.Y2), along = p1 - p0;
                    // COLR's linear gradient has a third point that tilts the colour lines; SVG's is the plain case,
                    // where that point is a quarter turn from the gradient vector.
                    Vector2 p2 = p0 + new Vector2(-along.Y, along.X);
                    colourAt = p => ColourAt(linear.Stops, linear.Extend, ColrRenderer.ProjectLinearGradient(Vector2.Transform(p, toGradient), p0, p1, p2));
                    break;
                }
                case RadialGradientPaint radial:
                {
                    if (!Matrix3x2.Invert(radial.Transform * toPixels, out Matrix3x2 toGradient)) return;
                    Vector2 c0 = new(radial.StartX, radial.StartY), c1 = new(radial.EndX, radial.EndY);
                    colourAt = p => ColourAt(radial.Stops, radial.Extend,
                        ColrRenderer.ProjectRadialGradient(Vector2.Transform(p, toGradient), c0, radial.StartRadius, c1, radial.EndRadius));
                    break;
                }
                case PatternPaint pattern:
                {
                    // Pattern space -> the shape's coordinates -> pixels; a pixel goes back the other way, into the tile.
                    Matrix3x2 patternToPixels = pattern.Transform * toPixels;
                    if (!Matrix3x2.Invert(patternToPixels, out Matrix3x2 toPattern)) return;
                    // The tile is drawn by a canvas of its own, so the coverage this paint is about to read is not disturbed.
                    if (TileOf(pattern, patternToPixels) is not { } tile) return;
                    colourAt = p => tile.At(Vector2.Transform(p, toPattern));
                    break;
                }
                default:
                    throw new NotSupportedException($"A paint of type {paint.GetType().Name} is not one this rasteriser knows.");
            }

            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    float coverage = _coverage[row + x];
                    if (coverage <= 0) continue;
                    Vector4 colour = colourAt is null ? solid : colourAt(new Vector2(x + 0.5f, y + 0.5f));
                    float a = colour.W * opacity * coverage;
                    if (a <= 0) continue;
                    int i = (row + x) * 4;
                    float keep = 1f - a;
                    layer[i] = colour.X * a + layer[i] * keep;
                    layer[i + 1] = colour.Y * a + layer[i + 1] * keep;
                    layer[i + 2] = colour.Z * a + layer[i + 2] * keep;
                    layer[i + 3] = a + layer[i + 3] * keep;
                }
            }
        }
    }

    private static Vector4 ToVector(SceneColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    /// <summary>
    /// The colour at an offset: the extend rule, then a straight blend of the two stops around it, channels and alpha
    /// alike and not premultiplied. NaN (no circle of a radial gradient passes through the point) is nothing.
    /// </summary>
    private static Vector4 ColourAt(ImmutableArray<GradientStop> stops, GradientExtend extend, float t)
    {
        if (float.IsNaN(t) || stops.IsDefaultOrEmpty) return default;
        switch (extend)
        {
            case GradientExtend.Repeat:
                t -= MathF.Floor(t);
                break;
            case GradientExtend.Reflect:
                t = MathF.Abs(t) % 2f;
                if (t > 1f) t = 2f - t;
                break;
            default:
                t = Math.Clamp(t, 0f, 1f);
                break;
        }
        if (t < stops[0].Offset) return ToVector(stops[0].Color);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t >= stops[i].Offset) continue;
            float span = stops[i].Offset - stops[i - 1].Offset;
            return Vector4.Lerp(ToVector(stops[i - 1].Color), ToVector(stops[i].Color), (t - stops[i - 1].Offset) / span);
        }
        return ToVector(stops[^1].Color);
    }

    // ---- Geometry --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plays a path into a glyph sink through a transform. For a fill every subpath is closed, which is what filling
    /// does with an open one and what the edge collector needs to be told.
    /// </summary>
    private static void Emit(VectorPath path, Matrix3x2 m, IGlyphSink sink, bool closeOpen)
    {
        ReadOnlySpan<float> c = path.Coordinates;
        bool open = false;
        int i = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                {
                    if (open && closeOpen) sink.Close();
                    Vector2 p = Vector2.Transform(new Vector2(c[i], c[i + 1]), m);
                    sink.MoveTo(p.X, p.Y);
                    open = true;
                    break;
                }
                case PathVerb.Line:
                {
                    Vector2 p = Vector2.Transform(new Vector2(c[i], c[i + 1]), m);
                    sink.LineTo(p.X, p.Y);
                    break;
                }
                case PathVerb.Quad:
                {
                    Vector2 q = Vector2.Transform(new Vector2(c[i], c[i + 1]), m), p = Vector2.Transform(new Vector2(c[i + 2], c[i + 3]), m);
                    sink.QuadTo(q.X, q.Y, p.X, p.Y);
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 q1 = Vector2.Transform(new Vector2(c[i], c[i + 1]), m), q2 = Vector2.Transform(new Vector2(c[i + 2], c[i + 3]), m);
                    Vector2 p = Vector2.Transform(new Vector2(c[i + 4], c[i + 5]), m);
                    sink.CubicTo(q1.X, q1.Y, q2.X, q2.Y, p.X, p.Y);
                    break;
                }
                default:
                    sink.Close();
                    open = false;
                    break;
            }
            i += VectorPath.CoordinateCount(verb);
        }
        if (open && closeOpen) sink.Close();
    }

    /// <summary>
    /// The stroke of a path as a filled outline in pixels.
    /// </summary>
    /// <remarks>
    /// The stroker flattens curves to a quarter of a unit, so it has to be fed something near pixel size. But a stroke
    /// is defined in the shape's own coordinates, and a transform that is not uniform distorts it (a circle's stroke
    /// under <c>scale(2, 1)</c> is thicker at the sides). Both are met by stroking in the shape's coordinates scaled
    /// UNIFORMLY to pixel size, and putting the outline through what is left of the transform.
    /// </remarks>
    private static void StrokeInto(VectorPath path, VectorStroke stroke, Matrix3x2 toPixels, IGlyphSink output)
    {
        float k = MathF.Sqrt(MathF.Abs(toPixels.M11 * toPixels.M22 - toPixels.M12 * toPixels.M21));
        if (!(k > 0) || !float.IsFinite(k) || !(stroke.Width > 0)) return;
        Matrix3x2 uniform = Matrix3x2.CreateScale(k);
        Matrix3x2 rest = Matrix3x2.CreateScale(1f / k) * toPixels;

        Action<IGlyphSink> source = sink => Emit(path, uniform, sink, closeOpen: false);
        if (!stroke.Dashes.IsDefaultOrEmpty)
        {
            Action<IGlyphSink> whole = source;
            source = sink => Dash(whole, stroke.Dashes, stroke.DashOffset, k, sink);
        }

        OutlineStroker.Stroke(source, new TransformSink(output, rest), stroke.Width * k,
            stroke.Cap switch { SceneLineCap.Round => FontLineCap.Round, SceneLineCap.Square => FontLineCap.Square, _ => FontLineCap.Butt },
            stroke.Join switch { SceneLineJoin.Round => FontLineJoin.Round, SceneLineJoin.Bevel => FontLineJoin.Bevel, _ => FontLineJoin.Miter },
            stroke.MiterLimit);
    }

    /// <summary>The most dashes one stroke is cut into. Past it the pattern is too fine to see and the line is drawn solid.</summary>
    private const int MaxDashes = 1 << 16;

    /// <summary>
    /// Cuts a path into the pieces a dash pattern leaves, as open polylines. The pattern starts again at every
    /// subpath, <paramref name="offset"/> into it; lengths are in the path's units times <paramref name="scale"/>.
    /// </summary>
    private static void Dash(Action<IGlyphSink> source, ImmutableArray<float> pattern, float offset, float scale, IGlyphSink sink)
    {
        var collector = new OutlineStroker.SegmentCollector();
        source(collector);

        float total = 0;
        foreach (float length in pattern) total += length * scale;
        float pathLength = 0;
        foreach (OutlineStroker.Contour contour in collector.Contours)
        {
            for (int i = 1; i < contour.Points.Count; i++)
            {
                pathLength += Vector2.Distance(new Vector2(contour.Points[i - 1].X, contour.Points[i - 1].Y), new Vector2(contour.Points[i].X, contour.Points[i].Y));
            }
        }
        if (!(total > 0) || !float.IsFinite(total) || pathLength / total * pattern.Length > MaxDashes)
        {
            // Nothing to cut with, or far too fine: the whole line.
            foreach (OutlineStroker.Contour contour in collector.Contours)
            {
                if (contour.Points.Count == 0) continue;
                sink.MoveTo(contour.Points[0].X, contour.Points[0].Y);
                for (int i = 1; i < contour.Points.Count; i++) sink.LineTo(contour.Points[i].X, contour.Points[i].Y);
                if (contour.Closed) sink.Close();
            }
            return;
        }

        foreach (OutlineStroker.Contour contour in collector.Contours)
        {
            // Where in the pattern this subpath starts.
            float into = (offset * scale) % total;
            if (into < 0) into += total;
            int index = 0;
            float left = pattern[0] * scale;
            // "into" is less than one whole pattern, so this passes each entry at most once.
            for (int step = 0; into > 0 && step <= pattern.Length; step++)
            {
                if (into >= left)
                {
                    into -= left;
                    index = (index + 1) % pattern.Length;
                    left = pattern[index] * scale;
                }
                else
                {
                    left -= into;
                    into = 0;
                }
            }
            bool on = index % 2 == 0, drawing = false;

            for (int i = 1; i < contour.Points.Count; i++)
            {
                Vector2 a = new(contour.Points[i - 1].X, contour.Points[i - 1].Y), b = new(contour.Points[i].X, contour.Points[i].Y);
                float length = Vector2.Distance(a, b), at = 0;
                if (length <= 0) continue;
                if (on && !drawing)
                {
                    sink.MoveTo(a.X, a.Y);
                    drawing = true;
                }
                while (length - at > left)
                {
                    at += left;
                    Vector2 p = Vector2.Lerp(a, b, at / length);
                    if (on) sink.LineTo(p.X, p.Y);
                    index = (index + 1) % pattern.Length;
                    left = pattern[index] * scale;
                    on = !on;
                    drawing = false;
                    if (on)
                    {
                        sink.MoveTo(p.X, p.Y);
                        drawing = true;
                    }
                }
                left -= length - at;
                if (on) sink.LineTo(b.X, b.Y);
            }
        }
    }

    /// <summary>Puts every point through a transform on its way to another sink.</summary>
    private sealed class TransformSink(IGlyphSink inner, Matrix3x2 m) : IGlyphSink
    {
        private Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), m);

        public void MoveTo(float x, float y)
        {
            Vector2 p = Map(x, y);
            inner.MoveTo(p.X, p.Y);
        }

        public void LineTo(float x, float y)
        {
            Vector2 p = Map(x, y);
            inner.LineTo(p.X, p.Y);
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Vector2 c = Map(cx, cy), p = Map(x, y);
            inner.QuadTo(c.X, c.Y, p.X, p.Y);
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Vector2 c1 = Map(c1x, c1y), c2 = Map(c2x, c2y), p = Map(x, y);
            inner.CubicTo(c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y);
        }

        public void Close() => inner.Close();
    }
}
