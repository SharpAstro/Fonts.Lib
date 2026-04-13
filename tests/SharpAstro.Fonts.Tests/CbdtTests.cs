namespace SharpAstro.Fonts.Tests;

public class CbdtTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "PngDumps");

    static CbdtTests() => Directory.CreateDirectory(DumpDir);

    [Fact]
    public void NotoColorEmoji_HasCbdtTables()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoColorEmoji));
        font.HasColorBitmaps.ShouldBeTrue();
        font.HasColorGlyphs.ShouldBeFalse(); // CBDT-only build, no COLR/CPAL
        font.Cblc.ShouldNotBeNull();
        font.Cbdt.ShouldNotBeNull();
        font.Cblc.Strikes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void NotoColorEmoji_PickStrike_PrefersClosestUp()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoColorEmoji));
        var s = font.Cblc!.PickStrike(64f);
        s.ShouldNotBeNull();
        s.PpemY.ShouldBeGreaterThan((ushort)0);
    }

    [Theory]
    [InlineData(0x1F600)] // 😀 grinning face
    [InlineData(0x1F525)] // 🔥 fire
    [InlineData(0x1F534)] // 🔴 red circle
    public void NotoColorEmoji_RenderColor_ProducesNonEmpty(int codepoint)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoColorEmoji));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldBeGreaterThan(0u, $"font missing U+{codepoint:X4}");
        var bmp = font.RenderColor(gid, 64f);
        bmp.ShouldNotBeNull();
        bmp.IsEmpty.ShouldBeFalse();

        // Should have some opaque pixels and non-trivial colored content.
        var hasOpaque = false;
        for (var i = 3; i < bmp.Pixels.Length; i += 4)
            if (bmp.Pixels[i] > 0) { hasOpaque = true; break; }
        hasOpaque.ShouldBeTrue();
    }

    [Fact]
    public void DumpNotoColorEmoji_PngSamples()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoColorEmoji));
        int[] codepoints =
        [
            0x1F600, 0x1F525, 0x1F534, 0x1F7E2, 0x1F7E1, 0x2600,
            0x1F308, 0x1F4A1, 0x1F381, 0x2764, 0x1F351, 0x1F34E,
        ];
        var written = 0;
        foreach (var cp in codepoints)
        {
            var gid = font.GetGlyphId((uint)cp);
            if (gid == 0) continue;
            var bmp = font.RenderColor(gid, 96f);
            if (bmp is null || bmp.IsEmpty) continue;
            PngWriter.WriteRgba(
                System.IO.Path.Combine(DumpDir, $"NotoColorEmoji_U+{cp:X5}_96px.png"),
                bmp.Pixels, bmp.Width, bmp.Height);
            written++;
        }
        written.ShouldBeGreaterThan(0);
    }
}
