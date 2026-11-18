using System.Collections.Immutable;
using System.Numerics;
using SharpAstro.Fonts.Color;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Scenes;

/// <summary>
/// Works a <see cref="VectorFilter"/> out on a picture: the steps in order, each from the picture or from earlier
/// steps, the last one's picture the result.
/// </summary>
/// <remarks>
/// <para>Every picture here is one buffer of <b>premultiplied</b> RGBA floats, <c>width * height * 4</c>, whose pixel
/// grid is the filter's own coordinates scaled and moved (never turned): a point <c>(x, y)</c> of the filter is the
/// buffer's <c>(x * kx + tx, y * ky + ty)</c>. Whoever calls draws the layer into such a buffer, and puts the result
/// where it belongs afterwards; that keeps blurs and offsets along the buffer's rows and columns whatever the
/// filter's transform is.</para>
/// <para><b>Linear light.</b> A step says whether it works on linear light. A picture is kept in the form the step
/// that made it worked in, with a flag, and is changed over only when a step of the other kind reads it; the layer
/// and the result are sRGB.</para>
/// <para><b>Edges are whole pixels.</b> A step's area is rounded to the nearest pixel edge, so a picture is either in
/// a pixel or not. Where an area lies between pixels a browser may differ by that pixel.</para>
/// </remarks>
internal static class SceneFilters
{
    private readonly record struct Picture(float[] Pixels, bool Linear);

    /// <summary>The filter's result for a layer: a new buffer, sRGB, premultiplied. The layer is not changed.</summary>
    public static float[] Apply(VectorFilter filter, float[] source, int width, int height, float kx, float ky, float tx, float ty)
    {
        ImmutableArray<VectorFilterPrimitive> steps = filter.Primitives;
        if (steps.IsDefaultOrEmpty) return new float[source.Length];

        var results = new Picture[steps.Length];
        float[]? alpha = null;
        for (int i = 0; i < steps.Length; i++)
        {
            VectorFilterPrimitive step = steps[i];
            bool linear = step.LinearRgb;
            float[] made;
            switch (step)
            {
                case FilterBlur blur:
                    made = Blur(Input(blur.Input, linear), width, height, blur.DeviationX * MathF.Abs(kx), blur.DeviationY * MathF.Abs(ky));
                    break;
                case FilterOffset offset:
                    made = Offset(Input(offset.Input, linear), width, height, offset.Dx * kx, offset.Dy * ky);
                    break;
                case FilterFlood flood:
                {
                    made = new float[source.Length];
                    Vector4 colour = Premultiplied(flood.Color, linear);
                    for (int p = 0; p < made.Length; p += 4) colour.CopyTo(made.AsSpan(p, 4));
                    break;
                }
                case FilterComposite composite:
                {
                    float[] a = Input(composite.Input, linear), b = Input(composite.Input2, linear);
                    made = new float[source.Length];
                    for (int p = 0; p < made.Length; p += 4)
                    {
                        Compositing.Composite(composite.Operator, At(a, p), At(b, p), composite.K1, composite.K2, composite.K3, composite.K4).CopyTo(made.AsSpan(p, 4));
                    }
                    break;
                }
                case FilterMerge merge:
                {
                    made = new float[source.Length];
                    foreach (VectorFilterInput input in merge.Inputs.IsDefault ? [] : merge.Inputs)
                    {
                        float[] top = Input(input, linear);
                        for (int p = 0; p < made.Length; p += 4)
                        {
                            if (top[p + 3] > 0f) Compositing.Over(At(made, p), At(top, p)).CopyTo(made.AsSpan(p, 4));
                        }
                    }
                    break;
                }
                case FilterColorMatrix matrix:
                    made = ColorMatrix(Input(matrix.Input, linear), matrix.Matrix);
                    break;
                case FilterBlend blend:
                {
                    float[] top = Input(blend.Input, linear), below = Input(blend.Input2, linear);
                    made = new float[source.Length];
                    for (int p = 0; p < made.Length; p += 4)
                    {
                        Compositing.Blend(blend.Mode, At(below, p), At(top, p)).CopyTo(made.AsSpan(p, 4));
                    }
                    break;
                }
                case FilterComponentTransfer transfer:
                    made = Transfer(Input(transfer.Input, linear), transfer);
                    break;
                default:
                    throw new NotSupportedException($"A filter step of type {step.GetType().Name} is not one this rasteriser knows.");
            }
            KeepTo(made, width, height, step.Region, kx, ky, tx, ty);
            results[i] = new Picture(made, linear);
        }

        Picture last = results[^1];
        return last.Linear ? Converted(last.Pixels, toLinear: false) : last.Pixels;

        // A picture a step reads, in the form the step works in. Never changed by the step: every step writes a new buffer.
        float[] Input(VectorFilterInput input, bool linear)
        {
            switch (input.Kind)
            {
                case FilterInputKind.SourceAlpha:
                    if (alpha is null)
                    {
                        alpha = new float[source.Length];
                        for (int p = 3; p < alpha.Length; p += 4) alpha[p] = source[p];
                    }
                    return alpha;           // black in either form
                case FilterInputKind.Result when (uint)input.Index < (uint)results.Length && results[input.Index].Pixels is { } pixels:
                    return results[input.Index].Linear == linear ? pixels : Converted(pixels, linear);
                default:
                    // The layer; and, for a scene put together wrongly, a step that is not there yet.
                    return linear ? Converted(source, toLinear: true) : source;
            }
        }
    }

    private static Vector4 At(float[] pixels, int p) => new(pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]);

    // ---- Linear light ------------------------------------------------------------------------------------------------

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private static Vector4 Premultiplied(Rgba32 colour, bool linear)
    {
        float r = colour.R / 255f, g = colour.G / 255f, b = colour.B / 255f, a = colour.A / 255f;
        if (linear) (r, g, b) = (ToLinear(r), ToLinear(g), ToLinear(b));
        return new Vector4(r * a, g * a, b * a, a);
    }

    /// <summary>A copy of a premultiplied picture with its colours taken from sRGB to linear light or back. The alpha stays.</summary>
    private static float[] Converted(float[] pixels, bool toLinear)
    {
        float[] converted = new float[pixels.Length];
        for (int p = 0; p < pixels.Length; p += 4)
        {
            float a = pixels[p + 3];
            if (a <= 0f) continue;
            for (int ch = 0; ch < 3; ch++)
            {
                float c = Math.Clamp(pixels[p + ch] / a, 0f, 1f);
                converted[p + ch] = (toLinear ? ToLinear(c) : ToSrgb(c)) * a;
            }
            converted[p + 3] = a;
        }
        return converted;
    }

    // ---- A step's area -----------------------------------------------------------------------------------------------

    /// <summary>Makes everything outside an area transparent.</summary>
    private static void KeepTo(float[] pixels, int width, int height, VectorRect area, float kx, float ky, float tx, float ty)
    {
        float ax = area.X * kx + tx, bx = area.Right * kx + tx, ay = area.Y * ky + ty, by = area.Bottom * ky + ty;
        int x0 = Edge(MathF.Min(ax, bx), width), x1 = Edge(MathF.Max(ax, bx), width);
        int y0 = Edge(MathF.Min(ay, by), height), y1 = Edge(MathF.Max(ay, by), height);
        if (!(area.Width > 0f) || !(area.Height > 0f)) x1 = x0;
        for (int y = 0; y < height; y++)
        {
            Span<float> row = pixels.AsSpan(y * width * 4, width * 4);
            if (y < y0 || y >= y1 || x1 <= x0)
            {
                row.Clear();
                continue;
            }
            row[..(x0 * 4)].Clear();
            row[(x1 * 4)..].Clear();
        }

        static int Edge(float v, int size) => float.IsNaN(v) ? 0 : (int)Math.Clamp(MathF.Round(v), 0f, size);
    }

    // ---- Blur --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The width of the box a blur of some deviation is made of, three times over: the Filter Effects specification's
    /// <c>d = floor(s * 3 * sqrt(2 pi) / 4 + 0.5)</c>. The specification allows the three boxes from a deviation of 2;
    /// a browser uses them from where the box is two pixels wide (a deviation of 0.8), measured (Chrome and Edge: an
    /// edge blurred by 1 comes out as boxes of 2, 2 and 3 give it, to the level, and not as the Gaussian does), and so
    /// does this. Below that the box would be one pixel, which blurs nothing, and the Gaussian itself is used.
    /// </summary>
    private static int BoxWidth(float deviation) => (int)MathF.Floor(deviation * 3f * MathF.Sqrt(2f * MathF.PI) / 4f + 0.5f);

    private static float[] Blur(float[] source, int width, int height, float deviationX, float deviationY)
    {
        float[] pixels = (float[])source.Clone();
        var line = new Vector4[Math.Max(width, height)];
        var scratch = new Vector4[line.Length];
        if (deviationX > 0f && float.IsFinite(deviationX))
        {
            for (int y = 0; y < height; y++) BlurLine(pixels, y * width * 4, 4, width, deviationX, line, scratch);
        }
        if (deviationY > 0f && float.IsFinite(deviationY))
        {
            for (int x = 0; x < width; x++) BlurLine(pixels, x * 4, width * 4, height, deviationY, line, scratch);
        }
        return pixels;
    }

    /// <summary>Blurs one row or column in place. What is outside the buffer counts as transparent.</summary>
    private static void BlurLine(float[] pixels, int start, int stride, int count, float deviation, Vector4[] line, Vector4[] scratch)
    {
        for (int i = 0, p = start; i < count; i++, p += stride) line[i] = At(pixels, p);

        int d = BoxWidth(deviation);
        if (d < 2)
        {
            int radius = (int)MathF.Ceiling(deviation * 3f);
            Span<float> kernel = stackalloc float[radius * 2 + 1];
            float sum = 0f;
            for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] = MathF.Exp(-(k * k) / (2f * deviation * deviation));
            for (int i = 0; i < count; i++)
            {
                Vector4 total = default;
                int from = Math.Max(0, i - radius), to = Math.Min(count - 1, i + radius);
                for (int j = from; j <= to; j++) total += line[j] * kernel[j - i + radius];
                scratch[i] = total / sum;
            }
            Array.Copy(scratch, line, count);
        }
        else
        {
            // Three boxes: for an odd d all three about the pixel; for an even one, one about the edge to its left, one
            // about the edge to its right, and one of d + 1 about the pixel.
            if (d % 2 == 1)
            {
                Box(line, scratch, count, -(d / 2), d / 2);
                Box(scratch, line, count, -(d / 2), d / 2);
                Box(line, scratch, count, -(d / 2), d / 2);
                Array.Copy(scratch, line, count);
            }
            else
            {
                Box(line, scratch, count, -(d / 2), d / 2 - 1);
                Box(scratch, line, count, -(d / 2) + 1, d / 2);
                Box(line, scratch, count, -(d / 2), d / 2);
                Array.Copy(scratch, line, count);
            }
        }

        for (int i = 0, p = start; i < count; i++, p += stride) line[i].CopyTo(pixels.AsSpan(p, 4));
    }

    /// <summary>Every pixel as the mean of those from <paramref name="lo"/> to <paramref name="hi"/> of it, those outside the line counting as nothing.</summary>
    private static void Box(Vector4[] from, Vector4[] to, int count, int lo, int hi)
    {
        float scale = 1f / (hi - lo + 1);
        Vector4 sum = default;
        // The window of pixel 0 to begin with, then moved along one pixel at a time.
        for (int j = Math.Max(0, lo); j <= Math.Min(count - 1, hi); j++) sum += from[j];
        for (int i = 0; i < count; i++)
        {
            to[i] = sum * scale;
            int leaves = i + lo, enters = i + hi + 1;
            if ((uint)leaves < (uint)count) sum -= from[leaves];
            if ((uint)enters < (uint)count) sum += from[enters];
        }
    }

    // ---- Offset ------------------------------------------------------------------------------------------------------

    private static float[] Offset(float[] source, int width, int height, float dx, float dy)
    {
        float[] pixels = new float[source.Length];
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) return pixels;
        int wholeX = (int)MathF.Round(dx), wholeY = (int)MathF.Round(dy);
        if (MathF.Abs(dx - wholeX) < 1e-3f && MathF.Abs(dy - wholeY) < 1e-3f)
        {
            // By whole pixels: rows moved as they are.
            for (int y = 0; y < height; y++)
            {
                int sy = y - wholeY;
                if ((uint)sy >= (uint)height) continue;
                int x0 = Math.Max(0, wholeX), x1 = Math.Min(width, width + wholeX);
                if (x1 <= x0) break;
                source.AsSpan((sy * width + x0 - wholeX) * 4, (x1 - x0) * 4).CopyTo(pixels.AsSpan((y * width + x0) * 4));
            }
            return pixels;
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Sample(source, width, height, x + 0.5f - dx, y + 0.5f - dy).CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
            }
        }
        return pixels;
    }

    /// <summary>The picture at a point (pixel centres at half-integers), between its four nearest pixels; transparent outside.</summary>
    public static Vector4 Sample(float[] pixels, int width, int height, float x, float y)
    {
        float fx = x - 0.5f, fy = y - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ux = fx - x0, uy = fy - y0;
        return Vector4.Lerp(
            Vector4.Lerp(Pixel(x0, y0), Pixel(x0 + 1, y0), ux),
            Vector4.Lerp(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), ux), uy);

        Vector4 Pixel(int px, int py) => (uint)px < (uint)width && (uint)py < (uint)height ? At(pixels, (py * width + px) * 4) : default;
    }

    // ---- Recolouring -------------------------------------------------------------------------------------------------

    private static float[] ColorMatrix(float[] source, ImmutableArray<float> m)
    {
        float[] pixels = new float[source.Length];
        if (m.IsDefault || m.Length != 20) return (float[])source.Clone();
        for (int p = 0; p < source.Length; p += 4)
        {
            float a = source[p + 3];
            // A transparent pixel has no colour, but the matrix may still give it one (its fifth column).
            float r = a > 0f ? source[p] / a : 0f, g = a > 0f ? source[p + 1] / a : 0f, b = a > 0f ? source[p + 2] / a : 0f;
            float na = Math.Clamp(m[15] * r + m[16] * g + m[17] * b + m[18] * a + m[19], 0f, 1f);
            if (na <= 0f) continue;
            pixels[p] = Math.Clamp(m[0] * r + m[1] * g + m[2] * b + m[3] * a + m[4], 0f, 1f) * na;
            pixels[p + 1] = Math.Clamp(m[5] * r + m[6] * g + m[7] * b + m[8] * a + m[9], 0f, 1f) * na;
            pixels[p + 2] = Math.Clamp(m[10] * r + m[11] * g + m[12] * b + m[13] * a + m[14], 0f, 1f) * na;
            pixels[p + 3] = na;
        }
        return pixels;
    }

    private static float[] Transfer(float[] source, FilterComponentTransfer transfer)
    {
        float[] pixels = new float[source.Length];
        for (int p = 0; p < source.Length; p += 4)
        {
            float a = source[p + 3];
            float r = a > 0f ? source[p] / a : 0f, g = a > 0f ? source[p + 1] / a : 0f, b = a > 0f ? source[p + 2] / a : 0f;
            float na = Function(transfer.Alpha, a);
            if (na <= 0f) continue;
            pixels[p] = Function(transfer.Red, r) * na;
            pixels[p + 1] = Function(transfer.Green, g) * na;
            pixels[p + 2] = Function(transfer.Blue, b) * na;
            pixels[p + 3] = na;
        }
        return pixels;
    }

    private static float Function(FilterTransferFunction function, float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        float value;
        switch (function.Kind)
        {
            case FilterTransferKind.Table when function.Table.Length > 0:
            {
                // n + 1 values over n equal parts of 0..1, straight lines between them.
                ImmutableArray<float> v = function.Table;
                int n = v.Length - 1;
                if (n == 0) { value = v[0]; break; }
                int k = Math.Min((int)(c * n), n - 1);
                value = v[k] + (c * n - k) * (v[k + 1] - v[k]);
                break;
            }
            case FilterTransferKind.Discrete when function.Table.Length > 0:
            {
                ImmutableArray<float> v = function.Table;
                value = v[Math.Min((int)(c * v.Length), v.Length - 1)];
                break;
            }
            case FilterTransferKind.Linear:
                value = function.Slope * c + function.Intercept;
                break;
            case FilterTransferKind.Gamma:
                value = function.Amplitude * MathF.Pow(c, function.Exponent) + function.Offset;
                break;
            default:
                value = c;
                break;
        }
        return float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// How far, in buffer pixels, a filter can bring something in from: what its blurs and offsets add up to. A layer
    /// drawn this much wider than what is to be shown has everything in it that the shown part can depend on.
    /// </summary>
    public static int Reach(VectorFilter filter, float kx, float ky)
    {
        double reach = 0;
        foreach (VectorFilterPrimitive step in filter.Primitives.IsDefault ? [] : filter.Primitives)
        {
            switch (step)
            {
                case FilterBlur blur:
                    reach += Math.Ceiling(3.0 * Math.Max(Math.Abs(blur.DeviationX * kx), Math.Abs(blur.DeviationY * ky))) + 1;
                    break;
                case FilterOffset offset:
                    reach += Math.Ceiling(Math.Max(Math.Abs(offset.Dx * kx), Math.Abs(offset.Dy * ky))) + 1;
                    break;
            }
        }
        return double.IsFinite(reach) ? (int)Math.Min(reach, 1 << 20) : 1 << 20;
    }
}
