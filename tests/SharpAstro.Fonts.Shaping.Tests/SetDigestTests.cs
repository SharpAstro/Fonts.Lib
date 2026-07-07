using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// <see cref="SetDigest"/> — the Bloom-filter-style glyph-set summary the <see cref="LookupRunner"/>
/// uses to skip a lookup at glyphs its coverage can't contain. The contract that makes skipping safe
/// is one-sided: <see cref="SetDigest.MayContain"/> must never return false for a glyph that was
/// added (no false negatives). These lock that down for single adds, contiguous ranges, and the
/// wrap-around case of the range bit-trick, plus the saturate-all fallback and the empty
/// "definitely absent" answer that gives the runner something to skip on.
/// </summary>
public class SetDigestTests
{
    [Fact]
    public void Add_HasNoFalseNegatives()
    {
        var digest = default(SetDigest);
        uint[] glyphs = [0, 1, 3, 42, 255, 256, 1000, 5000, 65535];
        foreach (var g in glyphs) digest.Add(g);
        foreach (var g in glyphs) digest.MayContain(g).ShouldBeTrue($"glyph {g} was added");
    }

    [Fact]
    public void AddRange_CoversEveryGlyphInRange()
    {
        var digest = default(SetDigest);
        digest.AddRange(100, 200);
        for (uint g = 100; g <= 200; g++) digest.MayContain(g).ShouldBeTrue($"glyph {g} is in [100, 200]");
    }

    [Fact]
    public void AddRange_CoversRangeThatWrapsABucketBoundary()
    {
        // Chosen so the shift-4 window's buckets run 60..63 then wrap to 0..6 without the range being
        // wide enough to saturate that window — the exact case the mb + (mb - ma) - (mb < ma) trick's
        // wrap correction handles. Every in-range glyph must still test present.
        var digest = default(SetDigest);
        digest.AddRange(960, 1135);
        for (uint g = 960; g <= 1135; g++) digest.MayContain(g).ShouldBeTrue($"glyph {g} is in the wrapping range");
    }

    [Fact]
    public void SaturateAll_MatchesEveryGlyph()
    {
        var digest = default(SetDigest);
        digest.SaturateAll();
        uint[] glyphs = [0, 1, 12345, 40000, 65535];
        foreach (var g in glyphs) digest.MayContain(g).ShouldBeTrue($"a saturated digest matches glyph {g}");
    }

    [Fact]
    public void Empty_ReportsGlyphsDefinitelyAbsent()
    {
        // All three masks are zero, so the filter can answer a hard "no" — this is the answer that
        // lets the runner skip a lookup's coverage probe and GDEF class lookup entirely.
        var digest = default(SetDigest);
        digest.MayContain(0).ShouldBeFalse();
        digest.MayContain(42).ShouldBeFalse();
        digest.MayContain(65535).ShouldBeFalse();
    }
}
