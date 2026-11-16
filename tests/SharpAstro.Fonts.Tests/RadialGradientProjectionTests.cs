using System.Numerics;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Where a point lies between the two circles of a radial gradient: the t for which it is on the circle with centre
/// lerp(c0, c1, t) and radius lerp(r0, r1, t). COLR glyphs and SVG gradients are both projected by this one function.
/// </summary>
public sealed class RadialGradientProjectionTests
{
    [Theory]
    // A point cone opening to the right: start (0,0) radius 0, end (10,0) radius 10. On the axis, |x - 10 t| = 10 t.
    [InlineData(15f, 0f, 0.75f)]
    [InlineData(20f, 0f, 1f)]
    [InlineData(5f, 0f, 0.25f)]
    // Off the axis: (10 t, 10 t) is straight above the centre of circle t, on it.
    [InlineData(5f, 5f, 0.5f)]
    [InlineData(8f, -8f, 0.8f)]
    public void APointBetweenCirclesThatDoNotShareACentre_IsOnTheSideTheCirclesMoveTo(float x, float y, float expected)
    {
        ColrRenderer.ProjectRadialGradient(new Vector2(x, y), new Vector2(0, 0), 0f, new Vector2(10, 0), 10f).ShouldBe(expected, 1e-4f);
    }

    [Fact]
    public void TheFocalSide_IsNotMirrored()
    {
        // A focal point left of centre: start (3,5) radius 0, end (5,5) radius 5. Going LEFT from the focus the end
        // circle is 3 away and going right it is 7 away, so the same distance from the focus is further along the
        // gradient on the left. A sign error in one term used to swap the two, which drew every such gradient
        // mirrored (a COLR emoji's glow came out on the wrong side of its source, or not at all).
        Vector2 focus = new(3, 5), centre = new(5, 5);
        float left = ColrRenderer.ProjectRadialGradient(new Vector2(1.5f, 5), focus, 0f, centre, 5f);
        float right = ColrRenderer.ProjectRadialGradient(new Vector2(4.5f, 5), focus, 0f, centre, 5f);
        left.ShouldBe(0.5f, 1e-4f);                 // 1.5 of the 3 to the edge
        right.ShouldBe(1.5f / 7f, 1e-4f);           // 1.5 of the 7 to the edge
        ColrRenderer.ProjectRadialGradient(new Vector2(0, 5), focus, 0f, centre, 5f).ShouldBe(1f, 1e-4f);
        ColrRenderer.ProjectRadialGradient(new Vector2(10, 5), focus, 0f, centre, 5f).ShouldBe(1f, 1e-4f);
    }

    [Theory]
    // Circles that share a centre: the plain radial gradient, which the sign error never touched.
    [InlineData(5f, 0f, 0.5f)]
    [InlineData(0f, -10f, 1f)]
    [InlineData(6f, 8f, 1f)]
    public void ConcentricCircles_AreDistanceOverRadius(float x, float y, float expected)
    {
        ColrRenderer.ProjectRadialGradient(new Vector2(x, y), Vector2.Zero, 0f, Vector2.Zero, 10f).ShouldBe(expected, 1e-4f);
    }
}
