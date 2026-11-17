using System.Buffers.Binary;
using System.Numerics;
using SharpAstro.Fonts.Tables.Svg;
using SharpAstro.VectorGraphics;
using Rgba32 = SharpAstro.Fonts.Color.Rgba32;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// The OpenType 'SVG ' table: finding a glyph's document, and what <see cref="SvgGlyphRenderer"/> and
/// <see cref="OpenTypeFont.RenderColor"/> make of it. Exact expectations, worked out by hand; the pictures as a whole
/// are held against browsers in <see cref="SvgGlyphFirefoxTests"/> and <see cref="SvgGlyphBrowserTests"/>.
/// </summary>
public sealed class SvgGlyphTests
{
    private const string Xmlns = "xmlns=\"http://www.w3.org/2000/svg\"";

    private static byte[] DejaVu => File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));

    private static OpenTypeFont FontWith(params (ushort First, ushort Last, byte[] Document)[] records) =>
        OpenTypeFont.Load(SyntheticSvgFont.WithTables(DejaVu, ("SVG ", SyntheticSvgFont.SvgTable(records))));

    private static Rgba32 At(ColorBitmap bitmap, int x, int y)
    {
        // x from the pen, y from the baseline with up negative: the glyph's own pixel grid.
        int column = x - bitmap.Left, row = y + bitmap.Top;
        if (column < 0 || row < 0 || column >= bitmap.Width || row >= bitmap.Height) return Rgba32.Transparent;
        ReadOnlySpan<byte> p = bitmap.Pixels.AsSpan((row * bitmap.Width + column) * 4, 4);
        return new Rgba32(p[0], p[1], p[2], p[3]);
    }

    // ---- The table ------------------------------------------------------------------------------------------------

    [Fact]
    public void AFontWithoutTheTable_HasNoSvgGlyphs_AndRendersAsBefore()
    {
        OpenTypeFont font = OpenTypeFont.Load(DejaVu);
        font.Svg.ShouldBeNull();
        font.HasSvgGlyphs.ShouldBeFalse();
        font.RenderColor(font.GetGlyphId('A'), 64).ShouldBeNull();
        SvgGlyphRenderer.TryRender(font, font.GetGlyphId('A'), 64).ShouldBeNull();
    }

    [Fact]
    public void TheTable_FindsEachGlyphsDocument_AndRecordsMayShareOne()
    {
        byte[] shared = SyntheticSvgFont.Utf8($"<svg {Xmlns}><g id=\"glyph5\"/><g id=\"glyph6\"/><g id=\"glyph40\"/></svg>");
        byte[] alone = SyntheticSvgFont.Utf8($"<svg {Xmlns} id=\"glyph9\"/>");
        SvgTable table = SvgTable.Parse(SyntheticSvgFont.SvgTable((5, 6, shared), (9, 9, alone), (40, 40, shared))).ShouldNotBeNull();
        table.RecordCount.ShouldBe(3);

        foreach (uint glyph in (uint[])[5, 6, 40])
        {
            table.HasGlyph(glyph).ShouldBeTrue();
            table.TryGetDocument(glyph, out ReadOnlyMemory<byte> document).ShouldBeTrue();
            document.ToArray().ShouldBe(shared);
        }
        table.TryGetDocument(9, out ReadOnlyMemory<byte> nine).ShouldBeTrue();
        nine.ToArray().ShouldBe(alone);
        foreach (uint glyph in (uint[])[0, 4, 7, 8, 10, 39, 41, 65535, 70000])
        {
            table.HasGlyph(glyph).ShouldBeFalse();
            table.TryGetDocument(glyph, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void RecordsOutOfOrderOrOverlapping_AreStillFound_TheOneWrittenFirstWinning()
    {
        byte[] a = SyntheticSvgFont.Utf8("<a/>"), b = SyntheticSvgFont.Utf8("<bb/>"), c = SyntheticSvgFont.Utf8("<ccc/>");
        SvgTable table = SvgTable.Parse(SyntheticSvgFont.SvgTable((20, 30, a), (5, 8, b), (25, 40, c))).ShouldNotBeNull();
        Document(table, 6).ShouldBe(b);
        Document(table, 20).ShouldBe(a);
        Document(table, 27).ShouldBe(a);        // in both ranges: the record that stands first
        Document(table, 35).ShouldBe(c);
        table.HasGlyph(9).ShouldBeFalse();
        table.HasGlyph(41).ShouldBeFalse();

        static byte[] Document(SvgTable table, uint glyph)
        {
            table.TryGetDocument(glyph, out ReadOnlyMemory<byte> document).ShouldBeTrue();
            return document.ToArray();
        }
    }

    [Fact]
    public void ARecordWhoseDocumentIsNotInTheTable_IsDropped_AndTheOthersStay()
    {
        byte[] good = SyntheticSvgFont.Utf8("<svg/>");
        byte[] table = SyntheticSvgFont.SvgTable((1, 1, good), (2, 2, good), (3, 3, good), (4, 4, good), (5, 5, good));
        // Record by record, from the list at offset 10: an offset past the end, a length past the end, a length of
        // nothing, a first glyph after the last. The first record is left alone.
        Span<byte> list = table.AsSpan(10);
        BinaryPrimitives.WriteUInt32BigEndian(list[(2 + 1 * 12 + 4)..], 0x7FFFFFF0);
        BinaryPrimitives.WriteUInt32BigEndian(list[(2 + 2 * 12 + 8)..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(list[(2 + 3 * 12 + 8)..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(list[(2 + 4 * 12)..], 9);

        SvgTable parsed = SvgTable.Parse(table).ShouldNotBeNull();
        parsed.RecordCount.ShouldBe(1);
        parsed.HasGlyph(1).ShouldBeTrue();
        foreach (uint glyph in (uint[])[2, 3, 4, 5, 9]) parsed.HasGlyph(glyph).ShouldBeFalse();
    }

    [Fact]
    public void ATableCutShortOrWithAnyByteChanged_NeverThrows_AndNeverReachesOutsideItself()
    {
        byte[] document = SyntheticSvgFont.Utf8($"<svg {Xmlns} id=\"glyph3\"><rect width=\"9\" height=\"9\"/></svg>");
        byte[] whole = SyntheticSvgFont.SvgTable((3, 3, document), (7, 9, document));

        for (int length = 0; length <= whole.Length; length++) Probe(whole.AsSpan(0, length).ToArray());
        for (int i = 0; i < whole.Length; i++)
        {
            foreach (byte flip in (byte[])[0x01, 0x80, 0xFF])
            {
                byte[] changed = (byte[])whole.Clone();
                changed[i] ^= flip;
                Probe(changed);
            }
        }

        static void Probe(byte[] table)
        {
            SvgTable? parsed = SvgTable.Parse(table);
            if (parsed is null) return;
            for (uint glyph = 0; glyph < 12; glyph++)
            {
                // Slicing throws if the record names bytes that are not there: Parse must have dropped it.
                if (parsed.TryGetDocument(glyph, out ReadOnlyMemory<byte> found)) found.Length.ShouldBeGreaterThan(0);
            }
            // And as a font's table, through the reader: whatever the bytes now say, no exception leaves.
            OpenTypeFont font = OpenTypeFont.Load(SyntheticSvgFont.WithTables(DejaVuBytes.Value, ("SVG ", table)));
            for (uint glyph = 0; glyph < 12; glyph++) _ = SvgGlyphRenderer.TryRender(font, glyph, 16);
        }
    }

    private static readonly Lazy<byte[]> DejaVuBytes = new(() => DejaVu);

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 10, 0, 0, 0, 0 })]                                 // a header and no list
    [InlineData(new byte[] { 0, 1, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0 })]                           // a version this does not know
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0 })]                            // the list inside the header
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0 })]                           // a list of nothing
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0xFF, 0xFF, 0, 1, 0, 1 })]         // 65535 records declared, room for none
    public void ATableWithNothingUsableInIt_IsNoTable(byte[] table)
    {
        SvgTable.Parse(table).ShouldBeNull();
        OpenTypeFont font = OpenTypeFont.Load(SyntheticSvgFont.WithTables(DejaVu, ("SVG ", table.Length == 0 ? [0] : table)));
        font.HasSvgGlyphs.ShouldBeFalse();
    }

    // ---- A glyph, exactly ---------------------------------------------------------------------------------------------

    [Fact]
    public void AGlyph_IsDrawnInFontUnitsWithYDown_ItsOriginOnTheBaseline()
    {
        // A 300 x 500 unit block standing on the baseline 200 units right of the origin, and a 100 unit square under it.
        // At 204.8 pixels an em (a tenth of a pixel a unit): 30 x 50 pixels, 20 from the pen, and 10 x 10 below.
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(
            $"<svg {Xmlns}><g id=\"glyph36\"><rect x=\"200\" y=\"-500\" width=\"300\" height=\"500\" fill=\"#c02040\"/>" +
            "<rect x=\"200\" y=\"0\" width=\"100\" height=\"100\" fill=\"#0060c0\"/></g></svg>")));
        font.UnitsPerEm.ShouldBe((ushort)2048);

        ColorBitmap bitmap = font.RenderColor(36, 204.8f).ShouldNotBeNull();
        (bitmap.Left, bitmap.Top, bitmap.Width, bitmap.Height).ShouldBe((20, 50, 30, 60));
        At(bitmap, 20, -50).ShouldBe(new Rgba32(0xC0, 0x20, 0x40, 255));
        At(bitmap, 49, -1).ShouldBe(new Rgba32(0xC0, 0x20, 0x40, 255));
        At(bitmap, 20, 0).ShouldBe(new Rgba32(0x00, 0x60, 0xC0, 255));
        At(bitmap, 29, 9).ShouldBe(new Rgba32(0x00, 0x60, 0xC0, 255));
        At(bitmap, 30, 0).A.ShouldBe((byte)0);

        // The scene is the same thing before it is drawn: font units, y down.
        VectorScene scene = SvgGlyphRenderer.TryReadScene(font, 36).ShouldNotBeNull();
        (scene.Width, scene.Height).ShouldBe((2048f, 2048f));
        VectorShape block = scene.Nodes[0].ShouldBeOfType<VectorShape>();
        block.Path.TryGetBounds(out VectorRect box).ShouldBeTrue();
        Vector2.Transform(new Vector2(box.X, box.Y), block.Transform).ShouldBe(new Vector2(200, -500));
        Vector2.Transform(new Vector2(box.Right, box.Bottom), block.Transform).ShouldBe(new Vector2(500, 0));
    }

    [Fact]
    public void AStroke_IsInsideTheBitmap_AllOfIt()
    {
        // A line with no area of its own, 640 units long and stroked 640 wide: at 64 pixels an em a 20 x 20 square
        // whose middle is 30 pixels above the baseline. Its outline alone has no height at all.
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(
            $"<svg {Xmlns} id=\"glyph36\"><path d=\"M 320 -960 L 960 -960\" stroke=\"#204060\" stroke-width=\"640\"/></svg>")));
        ColorBitmap bitmap = font.RenderColor(36, 64).ShouldNotBeNull();
        (bitmap.Left, bitmap.Top, bitmap.Width, bitmap.Height).ShouldBe((10, 40, 20, 20));
        At(bitmap, 10, -40).ShouldBe(new Rgba32(0x20, 0x40, 0x60, 255));
        At(bitmap, 29, -21).ShouldBe(new Rgba32(0x20, 0x40, 0x60, 255));
    }

    [Fact]
    public void CurrentColor_IsTheTextColourGiven_AndBlackWhenNoneIs()
    {
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(
            $"<svg {Xmlns} id=\"glyph36\"><rect y=\"-640\" width=\"640\" height=\"640\" fill=\"currentColor\"/></svg>")));
        At(font.RenderColor(36, 64).ShouldNotBeNull(), 5, -5).ShouldBe(new Rgba32(0, 0, 0, 255));
        At(SvgGlyphRenderer.TryRender(font, 36, 64).ShouldNotBeNull(), 5, -5).ShouldBe(new Rgba32(0, 0, 0, 255));
        At(SvgGlyphRenderer.TryRender(font, 36, 64, new Rgba32(10, 200, 30, 255)).ShouldNotBeNull(), 5, -5).ShouldBe(new Rgba32(10, 200, 30, 255));
        // A text colour that is itself half transparent stays so.
        At(SvgGlyphRenderer.TryRender(font, 36, 64, new Rgba32(10, 200, 30, 128)).ShouldNotBeNull(), 5, -5).ShouldBe(new Rgba32(10, 200, 30, 128));
    }

    [Fact]
    public void PaletteEntries_ReachAGlyphAsColorVariables_WithTheirAlpha_AndFallBackWhereThereIsNone()
    {
        // The specification's example 6, as three flat blocks: entries 0 and 1 by var() with a fallback, and an entry
        // the palette does not have.
        byte[] document = SyntheticSvgFont.Utf8(
            $"<svg {Xmlns} id=\"glyph36\"><rect y=\"-320\" width=\"320\" height=\"320\" fill=\"var(--color0,darkblue)\"/>" +
            "<rect x=\"320\" y=\"-320\" width=\"320\" height=\"320\" fill=\"var(--color1, #00aab3)\"/>" +
            "<rect x=\"640\" y=\"-320\" width=\"320\" height=\"320\" fill=\"var(--color2, #808080)\"/></svg>");
        byte[] cpal = SyntheticSvgFont.CpalTable(
            [new Rgba32(0x8B, 0x00, 0x00, 255), new Rgba32(0x00, 0xAA, 0xB3, 0x40)],
            [new Rgba32(0x80, 0x00, 0x80, 255), new Rgba32(0xD6, 0x70, 0xDA, 255)]);
        byte[] svg = SyntheticSvgFont.SvgTable((36, 36, document));
        OpenTypeFont font = OpenTypeFont.Load(SyntheticSvgFont.WithTables(DejaVu, ("SVG ", svg), ("CPAL", cpal)));
        font.HasColorGlyphs.ShouldBeFalse("there is a palette and no COLR");

        ColorBitmap first = font.RenderColor(36, 64).ShouldNotBeNull();
        At(first, 5, -5).ShouldBe(new Rgba32(0x8B, 0x00, 0x00, 255));
        At(first, 15, -5).ShouldBe(new Rgba32(0x00, 0xAA, 0xB3, 0x40));         // the entry's alpha comes with it
        At(first, 25, -5).ShouldBe(new Rgba32(0x80, 0x80, 0x80, 255));          // no such entry: the fallback

        ColorBitmap second = SvgGlyphRenderer.TryRender(font, 36, 64, paletteIndex: 1).ShouldNotBeNull();
        At(second, 5, -5).ShouldBe(new Rgba32(0x80, 0x00, 0x80, 255));
        At(second, 15, -5).ShouldBe(new Rgba32(0xD6, 0x70, 0xDA, 255));

        // A palette the font does not have, and a font with no palette at all: the fallbacks.
        foreach (ColorBitmap none in (ColorBitmap[])
            [
                SvgGlyphRenderer.TryRender(font, 36, 64, paletteIndex: 2).ShouldNotBeNull(),
                SvgGlyphRenderer.TryRender(font, 36, 64, paletteIndex: -1).ShouldNotBeNull(),
                OpenTypeFont.Load(SyntheticSvgFont.WithTables(DejaVu, ("SVG ", svg))).RenderColor(36, 64).ShouldNotBeNull(),
            ])
        {
            At(none, 5, -5).ShouldBe(new Rgba32(0x00, 0x00, 0x8B, 255));
            At(none, 15, -5).ShouldBe(new Rgba32(0x00, 0xAA, 0xB3, 255));
        }
    }

    [Fact]
    public void AGzipDocument_IsTheSameGlyph()
    {
        const string document = $"<svg {Xmlns} id=\"glyph36\"><circle cx=\"512\" cy=\"-512\" r=\"400\" fill=\"#336699\"/></svg>";
        ColorBitmap plain = FontWith((36, 36, SyntheticSvgFont.Utf8(document))).RenderColor(36, 48).ShouldNotBeNull();
        byte[] packed = SyntheticSvgFont.Gzip(document);
        packed.AsSpan(0, 3).ToArray().ShouldBe([0x1F, 0x8B, 0x08]);
        ColorBitmap gzip = FontWith((36, 36, packed)).RenderColor(36, 48).ShouldNotBeNull();
        (gzip.Left, gzip.Top, gzip.Width, gzip.Height).ShouldBe((plain.Left, plain.Top, plain.Width, plain.Height));
        gzip.Pixels.ShouldBe(plain.Pixels);
    }

    // ---- What cannot be drawn ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><g id=\"glyph37\"><rect width=\"9\" height=\"9\"/></g></svg>")]   // another glyph's element
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><g id=\"glyph36\"/></svg>")]                                      // the element, and nothing in it
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><g id=\"glyph36\"><rect width=\"9\"")]                            // not XML
    [InlineData("<html><body id=\"glyph36\"/></html>")]                                                                         // not SVG
    [InlineData("")]
    public void ADocumentWithNoPictureOfTheGlyph_IsNoColourGlyph_SoTheOutlineIsDrawn(string document)
    {
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(document.Length == 0 ? " " : document)));
        font.Svg.ShouldNotBeNull().HasGlyph(36).ShouldBeTrue();
        SvgGlyphRenderer.TryReadScene(font, 36).ShouldBeNull();
        SvgGlyphRenderer.TryRender(font, 36, 64).ShouldBeNull();
        font.RenderColor(36, 64).ShouldBeNull();
        font.RenderGlyph(36, 64).IsEmpty.ShouldBeFalse("glyph 36 of DejaVu Sans is the letter A, and its outline is still there");
    }

    [Fact]
    public void AGlyphThatClaimsToBeEnormous_IsDrawnNoFurtherThanAFewEmsOut()
    {
        // A block a million ems wide: the bitmap is bounded by the size asked for, not by what the document says.
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(
            $"<svg {Xmlns} id=\"glyph36\"><rect x=\"-2000000000\" y=\"-2000000000\" width=\"4000000000\" height=\"4000000000\" fill=\"#123456\"/></svg>")));
        ColorBitmap bitmap = font.RenderColor(36, 32).ShouldNotBeNull();
        bitmap.Width.ShouldBeInRange(64, 204);
        bitmap.Height.ShouldBeInRange(64, 204);
        At(bitmap, 0, 0).ShouldBe(new Rgba32(0x12, 0x34, 0x56, 255));

        // And sizes that are not sizes draw nothing, where they might have asked for an array.
        foreach (float size in (float[])[0, -5, float.NaN, float.PositiveInfinity]) SvgGlyphRenderer.TryRender(font, 36, size).ShouldBeNull();
    }

    [Fact]
    public void ADocumentOverTheReadersLimits_IsNoGlyph_NotAnException()
    {
        // Forty levels of doubling by use: 2^40 rectangles asked for by a few kilobytes.
        var document = new System.Text.StringBuilder($"<svg {Xmlns}><defs><rect id=\"u0\" width=\"9\" height=\"9\"/>");
        for (int i = 1; i <= 40; i++) document.Append($"<g id=\"u{i}\"><use href=\"#u{i - 1}\"/><use href=\"#u{i - 1}\"/></g>");
        document.Append("</defs><use id=\"glyph36\" href=\"#u40\"/></svg>");
        OpenTypeFont font = FontWith((36, 36, SyntheticSvgFont.Utf8(document.ToString())));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        font.RenderColor(36, 64).ShouldBeNull();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    // ---- With the other colour tables -------------------------------------------------------------------------------

    [Fact]
    public void ColrComesFirst_AndAGlyphItDoesNotHaveIsDrawnFromSvg()
    {
        byte[] colrFont = File.ReadAllBytes(Fixtures.Path(Fixtures.Noto_COLRv1));
        OpenTypeFont plain = OpenTypeFont.Load(colrFont);
        uint withColr = 0, without = 0;
        for (uint glyph = 1; glyph < plain.NumGlyphs && (withColr == 0 || without == 0); glyph++)
        {
            if (ColrRenderer.TryRender(plain, glyph, 32) is { IsEmpty: false }) withColr = withColr == 0 ? glyph : withColr;
            else if (without == 0) without = glyph;
        }
        withColr.ShouldNotBe(0u);
        without.ShouldNotBe(0u);

        string Block(uint glyph) => $"<svg {Xmlns} id=\"glyph{glyph}\"><rect y=\"-{plain.UnitsPerEm}\" width=\"{plain.UnitsPerEm}\" height=\"{plain.UnitsPerEm}\" fill=\"#fe01dc\"/></svg>";
        var records = new List<(ushort, ushort, byte[])>
        {
            ((ushort)withColr, (ushort)withColr, SyntheticSvgFont.Utf8(Block(withColr))),
            ((ushort)without, (ushort)without, SyntheticSvgFont.Utf8(Block(without))),
        };
        records.Sort();
        OpenTypeFont both = OpenTypeFont.Load(SyntheticSvgFont.WithTables(colrFont, ("SVG ", SyntheticSvgFont.SvgTable([.. records]))));

        // The COLR glyph is the COLR picture, to the byte, although an SVG description of it is there.
        ColorBitmap colr = plain.RenderColor(withColr, 32).ShouldNotBeNull();
        ColorBitmap stillColr = both.RenderColor(withColr, 32).ShouldNotBeNull();
        stillColr.Pixels.ShouldBe(colr.Pixels);
        SvgGlyphRenderer.TryRender(both, withColr, 32).ShouldNotBeNull();

        // The other glyph had no colour picture, and has the SVG one now.
        plain.RenderColor(without, 32).ShouldBeNull();
        ColorBitmap svg = both.RenderColor(without, 32).ShouldNotBeNull();
        (svg.Left, svg.Top, svg.Width, svg.Height).ShouldBe((0, 32, 32, 32));
        At(svg, 16, -16).ShouldBe(new Rgba32(0xFE, 0x01, 0xDC, 255));
    }

    [Fact]
    public void AVariationInstance_KeepsTheSvgGlyphs()
    {
        byte[] flex = File.ReadAllBytes(Fixtures.Path(Fixtures.RobotoFlex));
        OpenTypeFont font = OpenTypeFont.Load(SyntheticSvgFont.WithTables(flex, ("SVG ", SyntheticSvgFont.SvgTable(
            (36, 36, SyntheticSvgFont.Utf8($"<svg {Xmlns} id=\"glyph36\"><rect y=\"-500\" width=\"500\" height=\"500\" fill=\"#102030\"/></svg>"))))));
        OpenTypeFont bold = font.WithVariation(new Dictionary<string, float> { ["wght"] = 700 });
        bold.HasSvgGlyphs.ShouldBeTrue();
        // An SVG description does not vary: the instance draws the same picture.
        bold.RenderColor(36, 40).ShouldNotBeNull().Pixels.ShouldBe(font.RenderColor(36, 40).ShouldNotBeNull().Pixels);
    }
}
