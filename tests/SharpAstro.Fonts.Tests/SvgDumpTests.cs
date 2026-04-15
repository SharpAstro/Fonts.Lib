using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Dump glyph outlines to SVG files under <c>bin/.../SvgDumps/</c> for
/// visual verification of the outline parser and composite resolver.
///
/// These are not strict assertions — they pass as long as SVG generation
/// doesn't throw and the output is non-trivial. Open the produced files
/// in a browser or VS Code SVG preview.
/// </summary>
public class SvgDumpTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "SvgDumps");

    static SvgDumpTests() => Directory.CreateDirectory(DumpDir);

    [Theory]
    [InlineData(Fixtures.DejaVuSans,  "AaBbQg0987.,?éü")]
    [InlineData(Fixtures.Merida,      "AaBbQg0987")]
    [InlineData(Fixtures.SourceSans3, "AaBbQg0987.,?éü")]
    public void DumpAscii(string fontFile, string chars)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));

        var fontName = System.IO.Path.GetFileNameWithoutExtension(fontFile);
        var fontDir = System.IO.Path.Combine(DumpDir, fontName);
        Directory.CreateDirectory(fontDir);

        foreach (var ch in chars.EnumerateRunes())
        {
            var gid = font.GetGlyphId((uint)ch.Value);
            if (gid == 0) continue;

            // Format-agnostic: works for TT (DejaVu, Merida) and CFF (SourceSans).
            var svg = SvgGlyphWriter.ToSvg(
                sink => font.DrawGlyph(gid, sink),
                title: $"{fontName} U+{ch.Value:X4} '{ch}' gid={gid}");
            var safeName = SafeFileName($"U+{ch.Value:X4}_{ch}");
            File.WriteAllText(System.IO.Path.Combine(fontDir, safeName + ".svg"), svg);

            svg.ShouldStartWith("<?xml");
            svg.ShouldContain("<path");
        }
    }

    [Fact]
    public void DumpFirstHundredGlyphs_DejaVuSans()
    {
        // Useful for spotting outline-parser regressions across the GID space
        // (composite resolution, off-curve handling, very tall/wide glyphs).
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var dir = System.IO.Path.Combine(DumpDir, "DejaVuSans_first100");
        Directory.CreateDirectory(dir);

        for (uint gid = 0; gid < Math.Min((uint)100, font.NumGlyphs); gid++)
        {
            var outline = font.LoadGlyphOutline(gid);
            if (outline.IsEmpty) continue;
            var svg = SvgGlyphWriter.ToSvg(outline, title: $"gid={gid}");
            File.WriteAllText(System.IO.Path.Combine(dir, $"gid_{gid:D4}.svg"), svg);
        }
    }

    private static string SafeFileName(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '+' or '_' or '-' ? c : '_';
        }
        return new string(buf);
    }
}
