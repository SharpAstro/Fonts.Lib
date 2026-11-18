using System.Xml.Linq;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The font the SVG glyph tests draw from: DejaVu Sans with an 'SVG ' table and a two-palette 'CPAL' written here,
/// one letter a case. The em is 2048 units, and the cases are drawn at 64 pixels an em, so 32 units are one pixel
/// and edges on multiples of 32 lie on pixel edges.
/// </summary>
internal static class SvgGlyphFont
{
    private const string Xmlns = "version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    private const string Gradient =
        "<linearGradient id=\"grad\" x1=\"0%\" y1=\"0%\" x2=\"0%\" y2=\"100%\">" +
        "<stop offset=\"0%\" stop-color=\"darkblue\"/><stop offset=\"100%\" stop-color=\"#00aab3\"/></linearGradient>";

    /// <summary>The two palettes: three entries each, the third of the first half transparent.</summary>
    public static readonly Rgba32[][] Palettes =
    [
        [new(0xC0, 0x20, 0x20, 255), new(0x20, 0x60, 0xC0, 255), new(0x10, 0x90, 0x30, 128)],
        [new(0x80, 0x00, 0x80, 255), new(0xDA, 0x70, 0xD6, 255), new(0xE0, 0x90, 0x00, 255)],
    ];

    /// <summary>
    /// A case: the letter whose glyph it replaces, what it is about, and whether Firefox draws it as the specification
    /// says (measured with Firefox 160; see <see cref="SvgGlyphFirefoxTests"/> for what it does otherwise).
    /// </summary>
    public sealed record Case(char Letter, string About, bool Firefox = true);

    public static readonly Case[] Cases =
    [
        new('A', "drawn where it stands, above the baseline (the specification's example 2), with a gradient"),
        new('B', "moved up by the root's viewBox (example 3)"),
        new('C', "one of three in one document that share a piece in defs (example 4)"),
        new('D', "the second of the three"),
        new('E', "the third, with currentColor"),
        new('F', "palette entries by var(), one with alpha, one the palette does not have"),
        new('G', "a clip, a group's opacity, curves; below the baseline and left of the origin"),
        new('H', "the svg element is the glyph, and its viewBox is of another size than the em: the picture is scaled"),
        new('I', "an embedded PNG"),
        new('J', "the svg element itself is the glyph, and its properties are inherited"),
        new('K', "the glyph deep inside groups that are transformed: only its own transform applies"),
        new('L', "a style sheet and a style attribute"),
        new('M', "a gzip document"),
        new('N', "palette entries in a gradient's stops"),
        new('O', "a width and height on the root, and no viewBox: nothing is scaled"),
        new('P', "strokes: on a filled shape, alone, with round ends and round corners", Firefox: false),
        new('Q', "the glyph inside groups with a fill and an opacity: a use takes neither", Firefox: false),
        new('R', "a glyph that is not the root, under a root viewBox: the viewBox applies", Firefox: false),
    ];

    private static readonly Lazy<(byte[] Font, OpenTypeFont Loaded, Dictionary<char, string> Texts)> Built = new(Build);

    /// <summary>The font's bytes: what a browser is given.</summary>
    public static byte[] Bytes => Built.Value.Font;

    public static OpenTypeFont Font => Built.Value.Loaded;

    /// <summary>The glyph documents by the letters they describe, glyph ids filled in. One document may describe several letters.</summary>
    private static (string Letters, bool Gzip, Func<Func<char, uint>, string> Document)[] Documents =>
    [
        ("A", false, id => $"<svg id=\"glyph{id('A')}\" {Xmlns}><defs>{Gradient}</defs>" +
            "<rect x=\"192\" y=\"-896\" width=\"384\" height=\"896\" fill=\"url(#grad)\"/>" +
            "<rect x=\"192\" y=\"-1344\" width=\"384\" height=\"288\" fill=\"darkblue\"/></svg>"),

        ("B", false, id => $"<svg id=\"glyph{id('B')}\" {Xmlns} viewBox=\"0 2048 2048 2048\"><defs>{Gradient}</defs>" +
            "<rect x=\"192\" y=\"1152\" width=\"384\" height=\"896\" fill=\"url(#grad)\"/>" +
            "<rect x=\"192\" y=\"704\" width=\"384\" height=\"288\" fill=\"#a04000\"/></svg>"),

        ("CDE", false, id => $"<svg {Xmlns}><defs>{Gradient}" +
            "<g id=\"i-base\"><rect x=\"192\" y=\"1152\" width=\"384\" height=\"896\" fill=\"url(#grad)\"/></g></defs>" +
            $"<g id=\"glyph{id('C')}\" transform=\"translate(0,-2048)\"><use xlink:href=\"#i-base\"/></g>" +
            $"<g id=\"glyph{id('D')}\" transform=\"translate(0,-2048)\"><use xlink:href=\"#i-base\"/><rect x=\"192\" y=\"704\" width=\"384\" height=\"288\" fill=\"darkblue\"/></g>" +
            $"<g id=\"glyph{id('E')}\" transform=\"translate(0,-2048)\"><use xlink:href=\"#i-base\"/>" +
            "<polygon fill=\"currentColor\" points=\"224,992 544,992 864,672 416,672\"/></g></svg>"),

        ("F", false, id => $"<svg {Xmlns}><g id=\"glyph{id('F')}\">" +
            "<rect x=\"64\" y=\"-1280\" width=\"384\" height=\"1280\" fill=\"var(--color0, yellow)\"/>" +
            "<rect x=\"448\" y=\"-1280\" width=\"384\" height=\"1280\" fill=\"var(--color1,#00aab3)\"/>" +
            "<rect x=\"256\" y=\"-960\" width=\"384\" height=\"640\" fill=\"var(--color2, black)\"/>" +
            "<rect x=\"832\" y=\"-1280\" width=\"256\" height=\"1280\" fill=\"var(--color9, #909090)\"/></g></svg>"),

        ("G", false, id => $"<svg {Xmlns}><defs><clipPath id=\"c\"><circle cx=\"640\" cy=\"-640\" r=\"576\"/></clipPath></defs>" +
            $"<g id=\"glyph{id('G')}\"><g clip-path=\"url(#c)\"><rect x=\"0\" y=\"-1280\" width=\"1280\" height=\"1280\" fill=\"#d04020\"/>" +
            "<rect x=\"0\" y=\"-640\" width=\"1280\" height=\"640\" fill=\"#2040d0\"/></g>" +
            "<g opacity=\"0.5\"><rect x=\"-256\" y=\"-320\" width=\"640\" height=\"640\" fill=\"#108030\"/><rect x=\"64\" y=\"0\" width=\"640\" height=\"448\" fill=\"#f0d000\"/></g>" +
            "<path d=\"M 320 -1408 C 640 -1728 960 -1088 1280 -1408 L 1280 -1312 C 960 -992 640 -1632 320 -1312 Z\" fill=\"#202020\"/></g></svg>"),

        ("H", false, id => $"<svg id=\"glyph{id('H')}\" {Xmlns} viewBox=\"0 1024 1024 1024\">" +
            "<rect x=\"96\" y=\"384\" width=\"448\" height=\"640\" fill=\"#306090\"/><circle cx=\"320\" cy=\"704\" r=\"160\" fill=\"#f0c020\"/></svg>"),

        ("I", false, id => $"<svg id=\"glyph{id('I')}\" {Xmlns} viewBox=\"0 2048 2048 2048\">" +
            $"<image x=\"128\" y=\"1024\" width=\"768\" height=\"1024\" preserveAspectRatio=\"none\" xlink:href=\"{Picture()}\"/></svg>"),

        ("J", false, id => $"<svg id=\"glyph{id('J')}\" {Xmlns} fill=\"#207040\">" +
            "<rect x=\"128\" y=\"-1024\" width=\"512\" height=\"1024\"/><circle cx=\"384\" cy=\"-1280\" r=\"192\" fill=\"#c02020\"/></svg>"),

        ("K", false, id => $"<svg {Xmlns}><g transform=\"translate(640, 320) scale(0.5)\">" +
            $"<g transform=\"rotate(20)\"><g id=\"glyph{id('K')}\" transform=\"translate(64, 0)\">" +
            "<rect x=\"0\" y=\"-1280\" width=\"384\" height=\"1280\" fill=\"#c02020\"/><rect x=\"384\" y=\"-768\" width=\"512\" height=\"256\" fill=\"#2060c0\"/></g></g></g></svg>"),

        ("L", false, id => $"<svg {Xmlns}><style>.a {{ fill: #c06000 }} #dot {{ fill: #0060c0 }} g rect.b {{ fill: #208020 }}</style>" +
            $"<g id=\"glyph{id('L')}\"><rect class=\"a\" x=\"128\" y=\"-1280\" width=\"320\" height=\"1280\"/>" +
            "<rect class=\"b\" x=\"448\" y=\"-320\" width=\"512\" height=\"320\"/>" +
            "<circle id=\"dot\" cx=\"768\" cy=\"-896\" r=\"224\"/>" +
            "<rect x=\"1024\" y=\"-640\" width=\"192\" height=\"640\" style=\"fill: #802080; fill-opacity: 0.5\"/></g></svg>"),

        ("M", true, id => $"<svg {Xmlns}><g id=\"glyph{id('M')}\">" +
            "<path d=\"M 128 0 L 128 -1280 L 448 -1280 L 704 -640 L 960 -1280 L 1280 -1280 L 1280 0 L 992 0 L 992 -768 L 800 -256 L 608 -256 L 416 -768 L 416 0 Z\" fill=\"#404890\"/></g></svg>"),

        ("N", false, id => $"<svg {Xmlns}><defs><linearGradient id=\"p\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" +
            "<stop offset=\"0\" stop-color=\"var(--color0, darkblue)\"/><stop offset=\"0.5\" stop-color=\"var(--color1, #00aab3)\"/>" +
            "<stop offset=\"1\" stop-color=\"var(--color2, white)\"/></linearGradient></defs>" +
            $"<rect id=\"glyph{id('N')}\" x=\"64\" y=\"-1280\" width=\"1152\" height=\"1280\" fill=\"url(#p)\"/></svg>"),

        ("O", false, id => $"<svg {Xmlns} width=\"1024\" height=\"1024\"><g id=\"glyph{id('O')}\">" +
            "<rect x=\"96\" y=\"-1280\" width=\"448\" height=\"1280\" fill=\"#306090\"/><rect x=\"544\" y=\"-640\" width=\"448\" height=\"640\" fill=\"#903060\"/></g></svg>"),

        ("P", false, id => $"<svg {Xmlns}><g id=\"glyph{id('P')}\">" +
            "<rect x=\"128\" y=\"-1024\" width=\"384\" height=\"768\" fill=\"#f0c020\" stroke=\"#c02020\" stroke-width=\"128\"/>" +
            "<path d=\"M 704 -1024 L 704 -256\" stroke=\"#2060c0\" stroke-width=\"128\"/>" +
            "<path d=\"M 896 -1024 L 1088 -256\" fill=\"none\" stroke=\"#208020\" stroke-width=\"128\" stroke-linecap=\"round\"/>" +
            "<path d=\"M 128 -1536 L 448 -1216 L 768 -1536\" fill=\"none\" stroke=\"#801080\" stroke-width=\"160\" stroke-linejoin=\"round\"/>" +
            "<path d=\"M 192 64 C 512 -192 704 320 1024 64\" fill=\"none\" stroke=\"#202020\" stroke-width=\"96\" stroke-dasharray=\"192 96\"/></g></svg>"),

        ("Q", false, id => $"<svg {Xmlns}><g fill=\"#c02020\" opacity=\"0.4\"><g id=\"glyph{id('Q')}\">" +
            "<rect x=\"64\" y=\"-1280\" width=\"384\" height=\"1280\"/><rect x=\"448\" y=\"-768\" width=\"512\" height=\"256\" fill=\"#2060c0\"/></g></g></svg>"),

        ("R", false, id => $"<svg {Xmlns} viewBox=\"0 1024 1024 1024\"><g id=\"glyph{id('R')}\">" +
            "<rect x=\"96\" y=\"384\" width=\"448\" height=\"640\" fill=\"#609030\"/></g></svg>"),
    ];

    /// <summary>A small PNG as a data URI: 6 x 8 pixels in 2 x 2 blocks of flat colour, so that enlarging it changes edges and not areas.</summary>
    private static string Picture()
    {
        const int width = 6, height = 8;
        ReadOnlySpan<byte> palette =
        [
            200, 40, 40, 255, 40, 120, 200, 255, 250, 200, 30, 255,
            30, 150, 80, 255, 120, 40, 160, 255, 240, 240, 240, 255,
            20, 20, 20, 255, 240, 130, 30, 255, 0, 90, 160, 255,
            200, 0, 100, 255, 90, 90, 90, 255, 255, 255, 0, 255,
        ];
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                palette.Slice((y / 2 * 3 + x / 2) * 4, 4).CopyTo(rgba.AsSpan((y * width + x) * 4));
            }
        }
        return "data:image/png;base64," + Convert.ToBase64String(SharpAstro.Png.PngWriter.Encode(rgba, width, height));
    }

    public static uint GlyphOf(char letter) => Font.GetGlyphId(letter);

    /// <summary>The text of the document that describes a letter's glyph, as it was before any gzip.</summary>
    public static string DocumentOf(char letter) => Built.Value.Texts[letter];

    /// <summary>
    /// The picture of a glyph as the specification defines it, as an SVG document a browser that knows nothing of
    /// fonts can draw: "as though the given element and its content were specified in a defs tag and then referenced
    /// as the graphic content of an SVG document", in a viewport that is the em square and does not clip, the text
    /// colour as <c>color</c> and the palette as custom properties. The glyph's origin is put at
    /// (<paramref name="penX"/>, <paramref name="baseline"/>) of a <paramref name="width"/> x <paramref name="height"/>
    /// picture, at <paramref name="pixelsPerEm"/>.
    /// </summary>
    public static string EquivalentDocument(char letter, int width, int height, int penX, int baseline, float pixelsPerEm, Rgba32 colour, int palette)
    {
        XNamespace svg = "http://www.w3.org/2000/svg", xlink = "http://www.w3.org/1999/xlink";
        XElement root = XDocument.Parse(DocumentOf(letter)).Root!;
        string id = "glyph" + GlyphOf(letter);
        int em = Font.UnitsPerEm;

        var inner = new XElement(svg + "svg", root.Attributes().Where(a => !a.IsNamespaceDeclaration));
        if (inner.Attribute("width") is null) inner.SetAttributeValue("width", em);
        if (inner.Attribute("height") is null) inner.SetAttributeValue("height", em);
        inner.SetAttributeValue("overflow", "visible");
        if ((string?)root.Attribute("id") == id)
        {
            inner.Add(root.Nodes());
        }
        else
        {
            inner.Add(new XElement(svg + "defs", root.Nodes()), new XElement(svg + "use", new XAttribute(xlink + "href", "#" + id)));
        }

        string style = $"color: #{colour.R:x2}{colour.G:x2}{colour.B:x2};";
        if (palette >= 0)
        {
            for (int i = 0; i < Palettes[palette].Length; i++)
            {
                Rgba32 c = Palettes[palette][i];
                style += $" --color{i}: #{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2};";
            }
        }
        var outer = new XElement(svg + "svg",
            new XAttribute("width", width), new XAttribute("height", height), new XAttribute(XNamespace.Xmlns + "xlink", xlink.NamespaceName),
            new XAttribute("style", style),
            new XElement(svg + "g",
                new XAttribute("transform", FormattableString.Invariant($"translate({penX} {baseline}) scale({pixelsPerEm / em:R})")),
                inner));
        return outer.ToString(SaveOptions.DisableFormatting);
    }

    private static (byte[], OpenTypeFont, Dictionary<char, string>) Build()
    {
        byte[] plain = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        OpenTypeFont dejaVu = OpenTypeFont.Load(plain);
        uint Id(char letter) => dejaVu.GetGlyphId(letter);

        var records = new List<(ushort First, ushort Last, byte[] Document)>();
        var texts = new Dictionary<char, string>();
        foreach ((string letters, bool gzip, Func<Func<char, uint>, string> document) in Documents)
        {
            string text = document(Id);
            foreach (char letter in letters) texts[letter] = text;
            byte[] bytes = gzip ? SyntheticSvgFont.Gzip(text) : SyntheticSvgFont.Utf8(text);
            // One record a run of neighbouring glyph ids, all of a document's records sharing its bytes.
            uint[] ids = [.. letters.Select(Id).Order()];
            for (int i = 0; i < ids.Length;)
            {
                int end = i;
                while (end + 1 < ids.Length && ids[end + 1] == ids[end] + 1) end++;
                records.Add(((ushort)ids[i], (ushort)ids[end], bytes));
                i = end + 1;
            }
        }
        records.Sort((a, b) => a.First.CompareTo(b.First));

        byte[] font = SyntheticSvgFont.WithTables(plain,
            ("SVG ", SyntheticSvgFont.SvgTable([.. records])),
            ("CPAL", SyntheticSvgFont.CpalTable(Palettes)));
        return (font, OpenTypeFont.Load(font), texts);
    }
}
