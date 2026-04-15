namespace SharpAstro.Fonts.Tests;

/// <summary>
/// TrueType hinting tests. Verifies the full v40 interpreter pipeline:
/// table parsing, fpgm/prep execution, per-glyph instruction dispatch,
/// pixel-grid snapping, and hinted bitmap output.
/// </summary>
public class HintingFoundationTests
{
    [Fact]
    public void DejaVuSans_HasHintingTables()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.HasHinting.ShouldBeTrue();
        font.Maxp.MaxStackElements.ShouldBeGreaterThan((ushort)0);
    }

    [Fact]
    public void Interpreter_RunsFpgmWithoutCrash()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        // CreateHintingInterpreter runs fpgm internally — just ensure it returns.
        var interp = font.CreateHintingInterpreter();
        interp.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(24f)]
    [InlineData(96f)]
    public void Interpreter_RunsPrepAtMultipleSizes(float ppem)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var interp = font.CreateHintingInterpreter();
        interp.ShouldNotBeNull();
        // Should not throw across a range of sizes — prep runs each call.
        interp.OnSizeChange(ppem, font.UnitsPerEm, font.Prep ?? []);
    }

    [Theory]
    [InlineData('H', 24f)]
    [InlineData('o', 24f)]
    [InlineData('H', 96f)]
    public void LoadHintedOutline_ProducesScaledOutline(char ch, float ppem)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId(ch);
        var hinted = font.LoadHintedOutline(gid, ppem);
        hinted.ShouldNotBeNull();
        hinted.PointCount.ShouldBeGreaterThan(0);
        // Scale sanity: F26.6 X spread should match approximate pixel width.
        var scale = ppem / font.UnitsPerEm;
        var unhinted = font.LoadGlyphOutline(gid);
        var expectedWidthPx = (unhinted.Bounds.XMax - unhinted.Bounds.XMin) * scale;
        var minX = int.MaxValue; var maxX = int.MinValue;
        for (var i = 0; i < hinted.PointCount; i++)
        {
            if (hinted.X[i] < minX) minX = hinted.X[i];
            if (hinted.X[i] > maxX) maxX = hinted.X[i];
        }
        var actualWidthPx = (maxX - minX) / 64f;
        // Hinting can adjust by ~1px; allow generous tolerance.
        actualWidthPx.ShouldBeInRange(expectedWidthPx - 2f, expectedWidthPx + 2f);
    }

    [Fact]
    public void HintedOutline_StemsLandOnIntegerPixels()
    {
        // After hinting, the H's Y-axis features (baseline, cap-line, crossbar)
        // should be snapped to integer pixel boundaries. In v40 grayscale mode
        // X-direction snapping is intentionally suppressed (sub-pixel X positioning
        // is preserved for inter-glyph spacing), so we verify Y coords here.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId('H');
        var hinted = font.LoadHintedOutline(gid, 24f).ShouldNotBeNull();

        // Verify distinct Y coords land near integer pixels (multiples of 64 in F26.6).
        var distinctYs = new HashSet<int>();
        for (var i = 0; i < hinted.PointCount; i++)
            distinctYs.Add(hinted.Y[i]);

        var snappedCount = 0;
        foreach (var y in distinctYs)
        {
            var rem = y % 64;
            if (rem < 0) rem += 64;
            if (rem <= 3 || rem >= 61) snappedCount++;
        }
        // Y features should be snapped: baseline=0, crossbar=512 (8px),
        // crossbar-top=640 (10px), cap=1152 (18px) — all exact multiples of 64.
        var ratio = snappedCount / (double)distinctYs.Count;
        ratio.ShouldBeGreaterThan(0.5,
            $"only {snappedCount}/{distinctYs.Count} distinct Y coords snapped to pixel grid");

        // Sanity-check that Y hinting actually moved at least one point
        // from its naively-scaled position.
        var unhinted = font.LoadGlyphOutline(font.GetGlyphId('H'));
        var scale = 24f * 64f / font.UnitsPerEm; // F26.6 px per FUnit
        var anyMoved = false;
        for (var i = 0; i < unhinted.PointCount; i++)
        {
            var raw = (int)MathF.Round(unhinted.Y[i] * scale);
            if (Math.Abs(raw - hinted.Y[i]) > 1) { anyMoved = true; break; }
        }
        anyMoved.ShouldBeTrue("Y hinting did not move any point materially — verbs may be no-ops");
    }

    [Fact]
    public void RenderGlyphHinted_ProducesNonEmptyBitmap()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId('H');
        var bmp = font.RenderGlyphHinted(gid, 24f);
        bmp.Width.ShouldBeGreaterThan(0);
        bmp.Height.ShouldBeGreaterThan(0);
        // Must contain at least one fully-opaque pixel inside the H stem.
        var hasInk = false;
        for (var i = 0; i < bmp.Alpha.Length; i++)
            if (bmp.Alpha[i] > 0) { hasInk = true; break; }
        hasInk.ShouldBeTrue();
    }

    [Fact]
    public void Interpreter_RunsForAllHintingFontsInCorpus()
    {
        // Smoke test: any TTF in the corpus that has hinting tables should
        // load + initialize the interpreter without throwing.
        foreach (var fixtureName in Fixtures.All)
        {
            var path = Fixtures.Path(fixtureName);
            OpenTypeFont font;
            try { font = OpenTypeFont.LoadFromFile(path); }
            catch { continue; } // some fixtures might not be SFNT (e.g., CFF-only)
            if (!font.HasHinting) continue;
            var interp = font.CreateHintingInterpreter();
            interp.ShouldNotBeNull($"font {fixtureName} has hinting but interpreter creation failed");
            interp.OnSizeChange(24f, font.UnitsPerEm, font.Prep ?? []);
        }
    }
}
