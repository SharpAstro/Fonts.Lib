using System.Text;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The glyphs of an OpenType 'SVG ' table against the specification's own definition of their picture, drawn by a
/// browser that knows nothing of fonts.
/// </summary>
/// <remarks>
/// <para>The OpenType specification says what the picture of glyph N is by giving a document: the glyph's document
/// with everything put in <c>defs</c> and one <c>use</c> of the element <c>glyphN</c>, in a viewport that is the em
/// square and does not clip. <see cref="SvgGlyphFont.EquivalentDocument"/> writes that document (with the text colour
/// and the palette, as the specification says they reach a glyph), headless Chrome or Edge draws it, and
/// <see cref="SvgGlyphRenderer"/> has to draw the same from the font: the table found, the document unpacked, the element
/// picked, the y axis and the scale right, the colour and the palette given.</para>
/// <para>Chrome and Edge do not draw OpenType SVG glyphs themselves, which is why they are asked about the document and
/// not the font. Firefox draws them from the font, and <see cref="SvgGlyphFirefoxTests"/> holds it against that; but
/// Firefox has no palette, no text colour and no strokes in a glyph (measured), and those are held here alone.</para>
/// <para>Skipped where no browser is found, like <see cref="SceneRasterizerBrowserTests"/>.</para>
/// </remarks>
public sealed class SvgGlyphBrowserTests
{
    private const int Size = SvgGlyphFirefoxTests.Size, CellWidth = SvgGlyphFirefoxTests.CellWidth, CellHeight = SvgGlyphFirefoxTests.CellHeight;
    private const int PenX = SvgGlyphFirefoxTests.PenX, Baseline = SvgGlyphFirefoxTests.Baseline;

    /// <summary>The text colour and the palette each row of cases is drawn with.</summary>
    private static readonly (Rgba32 Colour, int Palette)[] Rows =
    [
        (new Rgba32(0, 0, 0, 255), 0),
        (new Rgba32(0xD0, 0x20, 0x40, 255), 1),
    ];

    public static TheoryData<char, int, string> Cells
    {
        get
        {
            var cells = new TheoryData<char, int, string>();
            for (int row = 0; row < Rows.Length; row++)
            {
                foreach (SvgGlyphFont.Case one in SvgGlyphFont.Cases) cells.Add(one.Letter, row, one.About);
            }
            return cells;
        }
    }

    private static readonly Lazy<byte[]> Captured = new(Capture);

    [Theory]
    [MemberData(nameof(Cells))]
    public void AGlyph_IsThePictureTheSpecificationDefines(char letter, int row, string about)
    {
        if (SceneRasterizerBrowserTests.Browser.Path is null)
        {
            Assert.Skip($"No Chromium-family browser found. Set {SceneRasterizerBrowserTests.Browser.Variable} to its executable, or install Chrome or Edge.");
            return;
        }

        int column = Array.FindIndex(SvgGlyphFont.Cases, one => one.Letter == letter);
        byte[] theirs = SvgGlyphFirefoxTests.Cell(Captured.Value, 4, Rows.Length * CellHeight, column, row);
        byte[] ours = SvgGlyphFirefoxTests.OurCell(letter, Rows[row].Colour, Rows[row].Palette);
        SvgGlyphFirefoxTests.Compare($"'{letter}' row {row} ({about})", ours, theirs);
    }

    private static byte[] Capture()
    {
        int width = SvgGlyphFont.Cases.Length * CellWidth, height = Rows.Length * CellHeight;
        var html = new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#fff\">");
        for (int row = 0; row < Rows.Length; row++)
        {
            for (int i = 0; i < SvgGlyphFont.Cases.Length; i++)
            {
                string svg = SvgGlyphFont.EquivalentDocument(SvgGlyphFont.Cases[i].Letter, CellWidth, CellHeight, PenX, Baseline, Size, Rows[row].Colour, Rows[row].Palette);
                html.Append($"<img style=\"position:absolute;left:{i * CellWidth}px;top:{row * CellHeight}px\" src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}\">");
            }
        }
        html.Append("</body></html>");
        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "svg-glyphs-equivalent.html"), html.ToString());
        }
        return SceneRasterizerBrowserTests.Browser.Capture(html.ToString(), width, height);
    }
}
