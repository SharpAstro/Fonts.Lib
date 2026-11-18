using System.Numerics;
using SharpAstro.Fonts.Tables.Colr;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Color;

/// <summary>
/// How one pixel is put on another: Porter and Duff's operators and the blend modes of the W3C Compositing and
/// Blending specification, for whoever draws layers here (the COLR paint graph's <c>PaintComposite</c>, a vector
/// scene's blended groups, a filter's composite and blend steps). One place, so that a mode means the same in all of
/// them.
/// </summary>
/// <remarks>
/// Every colour in and out is <b>premultiplied</b> RGBA with every channel 0 to 1 (X, Y, Z the colour times the
/// alpha, W the alpha). "Backdrop" is what is below, "source" what is put on it.
/// </remarks>
internal static class Compositing
{
    /// <summary>The source over the backdrop.</summary>
    public static Vector4 Over(Vector4 backdrop, Vector4 source) => source + backdrop * (1f - source.W);

    /// <summary>
    /// The source, its colour mixed with the backdrop's by <paramref name="mode"/> where both are there, over the
    /// backdrop: <c>Cs * as * (1 - ab) + B(Cb, Cs) * as * ab + Cb * ab * (1 - as)</c>, alpha as for plain "over".
    /// </summary>
    public static Vector4 Blend(VectorBlendMode mode, Vector4 backdrop, Vector4 source)
    {
        if (source.W <= 0f) return backdrop;
        if (mode == VectorBlendMode.Normal || backdrop.W <= 0f) return Over(backdrop, source);

        var cb = new Vector3(backdrop.X, backdrop.Y, backdrop.Z) / backdrop.W;
        var cs = new Vector3(source.X, source.Y, source.Z) / source.W;
        // What was premultiplied by a rounded alpha can come out a hair past 1.
        cb = Vector3.Clamp(cb, Vector3.Zero, Vector3.One);
        cs = Vector3.Clamp(cs, Vector3.Zero, Vector3.One);
        Vector3 mixed = mode switch
        {
            VectorBlendMode.Hue => SetLuminosity(SetSaturation(cs, Saturation(cb)), Luminosity(cb)),
            VectorBlendMode.Saturation => SetLuminosity(SetSaturation(cb, Saturation(cs)), Luminosity(cb)),
            VectorBlendMode.Color => SetLuminosity(cs, Luminosity(cb)),
            VectorBlendMode.Luminosity => SetLuminosity(cb, Luminosity(cs)),
            _ => new Vector3(Channel(mode, cb.X, cs.X), Channel(mode, cb.Y, cs.Y), Channel(mode, cb.Z, cs.Z)),
        };
        float both = source.W * backdrop.W;
        Vector3 colour = cs * (source.W - both) + mixed * both + cb * (backdrop.W - both);
        return new Vector4(colour, source.W + backdrop.W - both);
    }

    /// <summary>One channel of a mode that works channel by channel: <c>B(Cb, Cs)</c>, neither premultiplied.</summary>
    private static float Channel(VectorBlendMode mode, float b, float s) => mode switch
    {
        VectorBlendMode.Multiply => b * s,
        VectorBlendMode.Screen => b + s - b * s,
        VectorBlendMode.Overlay => HardLight(s, b),
        VectorBlendMode.Darken => MathF.Min(b, s),
        VectorBlendMode.Lighten => MathF.Max(b, s),
        VectorBlendMode.ColorDodge => b <= 0f ? 0f : s >= 1f ? 1f : MathF.Min(1f, b / (1f - s)),
        VectorBlendMode.ColorBurn => b >= 1f ? 1f : s <= 0f ? 0f : 1f - MathF.Min(1f, (1f - b) / s),
        VectorBlendMode.HardLight => HardLight(b, s),
        VectorBlendMode.SoftLight => s <= 0.5f
            ? b - (1f - 2f * s) * b * (1f - b)
            : b + (2f * s - 1f) * ((b <= 0.25f ? ((16f * b - 12f) * b + 4f) * b : MathF.Sqrt(b)) - b),
        VectorBlendMode.Difference => MathF.Abs(b - s),
        VectorBlendMode.Exclusion => b + s - 2f * b * s,
        _ => s,
    };

    /// <summary>Multiply by twice the source below a half of it, screen by twice the source less one above.</summary>
    private static float HardLight(float b, float s) => s <= 0.5f ? b * (2f * s) : b + (2f * s - 1f) - b * (2f * s - 1f);

    // The modes that are not channel by channel, as the specification writes them.

    private static float Luminosity(Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;

    private static float Saturation(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    /// <summary>The colour moved to a luminosity, then brought back inside 0..1 towards its own grey without changing that luminosity.</summary>
    private static Vector3 SetLuminosity(Vector3 c, float luminosity)
    {
        c += new Vector3(luminosity - Luminosity(c));
        float l = Luminosity(c), low = MathF.Min(c.X, MathF.Min(c.Y, c.Z)), high = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var grey = new Vector3(l);
        if (low < 0f) c = grey + (c - grey) * l / (l - low);
        if (high > 1f) c = grey + (c - grey) * (1f - l) / (high - l);
        return c;
    }

    /// <summary>The colour with its smallest channel at 0, its largest at the saturation, and the third between in proportion. Grey stays without colour.</summary>
    private static Vector3 SetSaturation(Vector3 c, float saturation)
    {
        float low = MathF.Min(c.X, MathF.Min(c.Y, c.Z)), high = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return high > low ? (c - new Vector3(low)) * saturation / (high - low) : Vector3.Zero;
    }

    /// <summary>
    /// A COLR <c>PaintComposite</c> mode: Porter and Duff's operators as they are, and every mode from
    /// <see cref="CompositeMode.Screen"/> on as the blend mode of that name.
    /// </summary>
    public static Vector4 Composite(CompositeMode mode, Vector4 backdrop, Vector4 source) => mode switch
    {
        CompositeMode.Clear => default,
        CompositeMode.Src => source,
        CompositeMode.Dest => backdrop,
        CompositeMode.SrcOver => Over(backdrop, source),
        CompositeMode.DestOver => Over(source, backdrop),
        CompositeMode.SrcIn => source * backdrop.W,
        CompositeMode.DestIn => backdrop * source.W,
        CompositeMode.SrcOut => source * (1f - backdrop.W),
        CompositeMode.DestOut => backdrop * (1f - source.W),
        CompositeMode.SrcAtop => source * backdrop.W + backdrop * (1f - source.W),
        CompositeMode.DestAtop => backdrop * source.W + source * (1f - backdrop.W),
        CompositeMode.Xor => source * (1f - backdrop.W) + backdrop * (1f - source.W),
        CompositeMode.Plus => Vector4.Min(source + backdrop, Vector4.One),
        CompositeMode.Screen => Blend(VectorBlendMode.Screen, backdrop, source),
        CompositeMode.Overlay => Blend(VectorBlendMode.Overlay, backdrop, source),
        CompositeMode.Darken => Blend(VectorBlendMode.Darken, backdrop, source),
        CompositeMode.Lighten => Blend(VectorBlendMode.Lighten, backdrop, source),
        CompositeMode.ColorDodge => Blend(VectorBlendMode.ColorDodge, backdrop, source),
        CompositeMode.ColorBurn => Blend(VectorBlendMode.ColorBurn, backdrop, source),
        CompositeMode.HardLight => Blend(VectorBlendMode.HardLight, backdrop, source),
        CompositeMode.SoftLight => Blend(VectorBlendMode.SoftLight, backdrop, source),
        CompositeMode.Difference => Blend(VectorBlendMode.Difference, backdrop, source),
        CompositeMode.Exclusion => Blend(VectorBlendMode.Exclusion, backdrop, source),
        CompositeMode.Multiply => Blend(VectorBlendMode.Multiply, backdrop, source),
        CompositeMode.HslHue => Blend(VectorBlendMode.Hue, backdrop, source),
        CompositeMode.HslSaturation => Blend(VectorBlendMode.Saturation, backdrop, source),
        CompositeMode.HslColor => Blend(VectorBlendMode.Color, backdrop, source),
        CompositeMode.HslLuminosity => Blend(VectorBlendMode.Luminosity, backdrop, source),
        // A mode the table has no name for: the source over the backdrop, as before.
        _ => Over(backdrop, source),
    };

    /// <summary>A filter's composite step: <paramref name="input"/> put on (in, out of, ...) <paramref name="input2"/>.</summary>
    public static Vector4 Composite(FilterCompositeOperator op, Vector4 input, Vector4 input2, float k1, float k2, float k3, float k4)
    {
        switch (op)
        {
            case FilterCompositeOperator.In: return input * input2.W;
            case FilterCompositeOperator.Out: return input * (1f - input2.W);
            case FilterCompositeOperator.Atop: return input * input2.W + input2 * (1f - input.W);
            case FilterCompositeOperator.Xor: return input * (1f - input2.W) + input2 * (1f - input.W);
            case FilterCompositeOperator.Lighter: return Vector4.Min(input + input2, Vector4.One);
            case FilterCompositeOperator.Arithmetic:
            {
                Vector4 result = Vector4.Clamp(k1 * input * input2 + k2 * input + k3 * input2 + new Vector4(k4), Vector4.Zero, Vector4.One);
                // A premultiplied colour is never more than its alpha.
                return Vector4.Min(result, new Vector4(result.W));
            }
            default: return Over(input2, input);
        }
    }
}
