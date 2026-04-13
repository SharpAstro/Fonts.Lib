using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tests;

public class VariableFontTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");

    static VariableFontTests() => Directory.CreateDirectory(DumpDir);

    [Fact]
    public void RobotoFlex_IsVariable_HasExpectedAxes()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        font.IsVariable.ShouldBeTrue();
        font.Fvar.ShouldNotBeNull();
        font.Gvar.ShouldNotBeNull();

        // Roboto Flex has at least these standard axes.
        font.Fvar.FindAxisIndex(Tag.Parse("wght")).ShouldBeGreaterThanOrEqualTo(0);
        font.Fvar.FindAxisIndex(Tag.Parse("wdth")).ShouldBeGreaterThanOrEqualTo(0);
        font.Fvar.FindAxisIndex(Tag.Parse("opsz")).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void RobotoFlex_DefaultVariation_OutlineMatchesBase()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var withDefault = font.WithVariation(new Dictionary<string, float>());
        // No active variation → IsVariationActive should be false.
        withDefault.IsVariationActive.ShouldBeFalse();

        var gid = font.GetGlyphId('A');
        var baseOutline = font.LoadGlyphOutline(gid);
        var defaultOutline = withDefault.LoadGlyphOutline(gid);

        defaultOutline.PointCount.ShouldBe(baseOutline.PointCount);
        defaultOutline.X.ToArray().ShouldBe(baseOutline.X.ToArray());
        defaultOutline.Y.ToArray().ShouldBe(baseOutline.Y.ToArray());
    }

    [Fact]
    public void RobotoFlex_HeavyWeight_OutlineDiffersFromLight()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var wghtAxis = font.Fvar!.Axes[font.Fvar.FindAxisIndex(Tag.Parse("wght"))];
        var light = font.WithVariation(new Dictionary<string, float> { ["wght"] = wghtAxis.Min });
        var heavy = font.WithVariation(new Dictionary<string, float> { ["wght"] = wghtAxis.Max });

        light.IsVariationActive.ShouldBeTrue();
        heavy.IsVariationActive.ShouldBeTrue();

        var gid = font.GetGlyphId('A');
        var lightOutline = light.LoadGlyphOutline(gid);
        var heavyOutline = heavy.LoadGlyphOutline(gid);

        // Same topology (point + contour count must remain stable across instances).
        lightOutline.PointCount.ShouldBe(heavyOutline.PointCount);
        lightOutline.ContourCount.ShouldBe(heavyOutline.ContourCount);

        // But coords must differ — heavy weight thickens the strokes.
        var lx = lightOutline.X.ToArray();
        var hx = heavyOutline.X.ToArray();
        var anyDifferent = false;
        for (var i = 0; i < lx.Length; i++)
            if (lx[i] != hx[i]) { anyDifferent = true; break; }
        anyDifferent.ShouldBeTrue("heavy and light weight outlines must differ");
    }

    [Fact]
    public void RobotoFlex_WithVariation_IsImmutableOriginal()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var heavy = font.WithVariation(new Dictionary<string, float> { ["wght"] = 1000 });

        // Original instance must still report no active variation.
        font.IsVariationActive.ShouldBeFalse();
        heavy.IsVariationActive.ShouldBeTrue();
    }

    [Fact]
    public void RobotoFlex_DumpThreeWeights()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var wghtAxis = font.Fvar!.Axes[font.Fvar.FindAxisIndex(Tag.Parse("wght"))];
        foreach (var (name, value) in new[]
                 {
                     ("Light",   wghtAxis.Min),
                     ("Regular", wghtAxis.Default),
                     ("Heavy",   wghtAxis.Max),
                 })
        {
            var inst = font.WithVariation(new Dictionary<string, float> { ["wght"] = value });
            foreach (var ch in "AaBbQg")
            {
                var gid = inst.GetGlyphId(ch);
                if (gid == 0) continue;
                var bmp = inst.RenderGlyph(gid, 64f);
                if (bmp.IsEmpty) continue;
                BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir,
                    $"RobotoFlex_{name}_U+{(int)ch:X4}_{ch}_64px.bmp"),
                    bmp.Alpha, bmp.Width, bmp.Height);
            }
        }
    }

    [Fact]
    public void RobotoFlex_VariationIsConcurrentlySafe()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var wghtAxis = font.Fvar!.Axes[font.Fvar.FindAxisIndex(Tag.Parse("wght"))];
        var heavy = font.WithVariation(new Dictionary<string, float> { ["wght"] = wghtAxis.Max });
        var gid = heavy.GetGlyphId('M');
        var expected = heavy.LoadGlyphOutline(gid);

        Parallel.For(0, 128, _ =>
        {
            var actual = heavy.LoadGlyphOutline(gid);
            actual.PointCount.ShouldBe(expected.PointCount);
            actual.X.ToArray().ShouldBe(expected.X.ToArray());
        });
    }
}
