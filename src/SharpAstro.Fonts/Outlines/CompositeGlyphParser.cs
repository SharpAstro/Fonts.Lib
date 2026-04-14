using System.Runtime.InteropServices;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Parses a "composite" glyph (numContours == -1) from the 'glyf' table by
/// resolving its referenced sub-glyphs and concatenating their points after
/// applying each component's affine transform.
///
/// Spec: https://learn.microsoft.com/typography/opentype/spec/glyf#composite-glyph-table
///
/// Stateless — safe to call from any thread provided <paramref name="loader"/>
/// is itself thread-safe (it should be: it's just a closure over loca + glyf
/// span access).
/// </summary>
internal static class CompositeGlyphParser
{
    // Composite flag bits.
    private const ushort ArgsAreWords         = 0x0001;
    private const ushort ArgsAreXYValues      = 0x0002;
    private const ushort RoundXYToGrid        = 0x0004; // ignored — for hinting
    private const ushort WeHaveAScale         = 0x0008;
    private const ushort MoreComponents       = 0x0020;
    private const ushort WeHaveAnXAndYScale   = 0x0040;
    private const ushort WeHaveATwoByTwo      = 0x0080;
    private const ushort WeHaveInstructions   = 0x0100;
    private const ushort UseMyMetrics         = 0x0200; // ignored at this layer
    private const ushort OverlapCompound      = 0x0400; // ignored
    private const ushort ScaledComponentOffset      = 0x0800;
    private const ushort UnscaledComponentOffset    = 0x1000;

    public delegate Outline GlyphLoader(uint glyphId);

    /// <param name="componentOffsetDeltas">
    /// Per-component (dx, dy) variation deltas from gvar. When non-null, element
    /// [i] is added to the i-th component's XY translation offsets before
    /// compositing. Only applied when <c>ArgsAreXYValues</c> is set on the
    /// component (anchor-point composites ignore this field). Null = no variation.
    /// </param>
    public static Outline Parse(ref BigEndianReader r,
        (short xMin, short yMin, short xMax, short yMax) bounds,
        GlyphLoader loader,
        (float Dx, float Dy)[]? componentOffsetDeltas = null)
    {
        var allX = new List<short>(64);
        var allY = new List<short>(64);
        var allFlags = new List<byte>(64);
        var allEnds = new List<int>(8);

        ushort flag;
        var componentIndex = 0;
        do
        {
            flag = r.ReadUInt16();
            var glyphIndex = r.ReadUInt16();

            int arg1, arg2;
            if ((flag & ArgsAreWords) != 0)
            {
                arg1 = r.ReadInt16();
                arg2 = r.ReadInt16();
            }
            else
            {
                arg1 = r.ReadSByte();
                arg2 = r.ReadSByte();
            }

            float xx = 1f, xy = 0f, yx = 0f, yy = 1f;
            if ((flag & WeHaveAScale) != 0)
            {
                xx = yy = r.ReadF2Dot14();
            }
            else if ((flag & WeHaveAnXAndYScale) != 0)
            {
                xx = r.ReadF2Dot14();
                yy = r.ReadF2Dot14();
            }
            else if ((flag & WeHaveATwoByTwo) != 0)
            {
                xx = r.ReadF2Dot14();
                xy = r.ReadF2Dot14();
                yx = r.ReadF2Dot14();
                yy = r.ReadF2Dot14();
            }

            // Recursively load the referenced sub-glyph (already a fully
            // resolved Outline if it was composite itself).
            var sub = loader((uint)glyphIndex);
            if (sub.IsEmpty) { componentIndex++; continue; }

            var basePointIndex = allX.Count;

            // Determine translation (dx, dy) from arg1/arg2.
            // ArgsAreXYValues=1 → arg1, arg2 are x, y offsets.
            // Otherwise they are point indices (anchor points) — anchored matching.
            // We support the common XY-values case fully; anchor points are
            // resolved against already-emitted parent points.
            float dx = 0f, dy = 0f;
            if ((flag & ArgsAreXYValues) != 0)
            {
                dx = arg1;
                dy = arg2;

                // Apply gvar component-offset deltas when present (spec §gvar composite).
                if (componentOffsetDeltas is not null
                    && (uint)componentIndex < (uint)componentOffsetDeltas.Length)
                {
                    dx += componentOffsetDeltas[componentIndex].Dx;
                    dy += componentOffsetDeltas[componentIndex].Dy;
                }

                // Spec: when ScaledComponentOffset is set, the offset is in the
                // sub-glyph's coordinate system and must be transformed.
                // UnscaledComponentOffset overrides; default is unscaled.
                if ((flag & ScaledComponentOffset) != 0
                    && (flag & UnscaledComponentOffset) == 0)
                {
                    var tx = xx * dx + xy * dy;
                    var ty = yx * dx + yy * dy;
                    dx = tx;
                    dy = ty;
                }
            }
            else
            {
                // Anchor-point composite: arg1 is an index into already-emitted
                // points of the parent, arg2 is an index into the sub-glyph.
                if (arg1 < 0 || arg1 >= allX.Count
                    || arg2 < 0 || arg2 >= sub.PointCount)
                {
                    componentIndex++;
                    continue; // malformed; skip component
                }
                var parentX = allX[arg1];
                var parentY = allY[arg1];
                var subX = sub.X[arg2];
                var subY = sub.Y[arg2];
                // After transform of the anchor sub-point, translation lines them up.
                var transformedSubX = xx * subX + xy * subY;
                var transformedSubY = yx * subX + yy * subY;
                dx = parentX - transformedSubX;
                dy = parentY - transformedSubY;
            }

            componentIndex++;

            for (var i = 0; i < sub.PointCount; i++)
            {
                var sx = sub.X[i];
                var sy = sub.Y[i];
                var nx = xx * sx + xy * sy + dx;
                var ny = yx * sx + yy * sy + dy;
                allX.Add((short)Math.Clamp(MathF.Round(nx), short.MinValue, short.MaxValue));
                allY.Add((short)Math.Clamp(MathF.Round(ny), short.MinValue, short.MaxValue));
                allFlags.Add(sub.Flags[i]);
            }

            foreach (var end in sub.ContourEnds)
                allEnds.Add(basePointIndex + end);
        } while ((flag & MoreComponents) != 0);

        // Composite-level instructions hint the assembled outline (per spec
        // §"Composite glyph hinting"). Captured for Phase 8.
        byte[]? compositeInstructions = null;
        if ((flag & WeHaveInstructions) != 0)
        {
            int n = r.ReadUInt16();
            if (n > 0)
            {
                compositeInstructions = new byte[n];
                for (var i = 0; i < n; i++) compositeInstructions[i] = r.ReadByte();
            }
        }

        return new Outline(
            ImmutableCollectionsMarshal.AsImmutableArray(allX.ToArray()),
            ImmutableCollectionsMarshal.AsImmutableArray(allY.ToArray()),
            ImmutableCollectionsMarshal.AsImmutableArray(allFlags.ToArray()),
            ImmutableCollectionsMarshal.AsImmutableArray(allEnds.ToArray()),
            bounds, compositeInstructions);
    }
}
