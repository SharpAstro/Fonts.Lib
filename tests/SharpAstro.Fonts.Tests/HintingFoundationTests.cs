namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Phase 8 foundation smoke tests. The interpreter doesn't yet produce
/// hinted output (most hinting opcodes are no-ops); these tests just
/// verify the table-parsing + dispatcher path is structurally sound and
/// can run real-world fpgm/prep without crashing.
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
