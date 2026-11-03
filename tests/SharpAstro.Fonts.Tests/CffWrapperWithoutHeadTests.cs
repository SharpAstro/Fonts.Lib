using System.Buffers.Binary;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// An 'OTTO' face with no 'head' and no 'maxp' is still a whole font: everything those two tables
/// say is in the CFF. PDF producers embed such wrappers as the <c>/FontFile3 /OpenType</c> program
/// of a simple font, keeping only 'CFF ' and 'cmap', and refusing them sent every glyph of the
/// document to a substitute face. The fixture is built here from the bundled SIL-OFL SourceSans3
/// (its own 'CFF ' and 'cmap' re-wrapped), so no proprietary font is committed.
/// </summary>
public class CffWrapperWithoutHeadTests
{
    private static OpenTypeFont Full() => OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));

    /// <summary>An 'OTTO' sfnt holding only the given tables, in tag order, checksums left zero.</summary>
    private static byte[] Wrap(params (string Tag, byte[] Data)[] tables)
    {
        var sorted = tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToArray();
        var headerLength = 12 + 16 * sorted.Length;
        var length = headerLength + sorted.Sum(t => (t.Data.Length + 3) & ~3);
        var bytes = new byte[length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x4F54544F); // 'OTTO'
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)sorted.Length);
        var offset = headerLength;
        for (var i = 0; i < sorted.Length; i++)
        {
            var record = span[(12 + 16 * i)..];
            for (var c = 0; c < 4; c++) record[c] = (byte)sorted[i].Tag[c];
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)sorted[i].Data.Length);
            sorted[i].Data.CopyTo(span[offset..]);
            offset += (sorted[i].Data.Length + 3) & ~3;
        }
        return bytes;
    }

    private static byte[] Table(OpenTypeFont font, string tag)
    {
        font.TryGetTable(new Tag(tag), out var data).ShouldBeTrue($"fixture should have '{tag}'");
        return data.ToArray();
    }

    [Fact]
    public void CffAndCmapOnly_Loads_WithTheFullFacesMetrics()
    {
        var full = Full();
        var wrapped = OpenTypeFont.Load(Wrap(("CFF ", Table(full, "CFF ")), ("cmap", Table(full, "cmap"))));

        wrapped.HasCffOutlines.ShouldBeTrue();
        wrapped.NumGlyphs.ShouldBe(full.NumGlyphs);
        wrapped.UnitsPerEm.ShouldBe(full.UnitsPerEm);
        // The wrapper's own cmap is kept, and glyph names still come from the CFF charset.
        wrapped.GetGlyphId('A').ShouldBe(full.GetGlyphId('A'));
        wrapped.GetGlyphId('A').ShouldNotBe(0u);
        wrapped.GetGlyphIdByName("A").ShouldBe(full.GetGlyphIdByName("A"));
    }

    [Fact]
    public void CffAndCmapOnly_RendersIdenticalPixels_ToTheFullFace()
    {
        var full = Full();
        var wrapped = OpenTypeFont.Load(Wrap(("CFF ", Table(full, "CFF ")), ("cmap", Table(full, "cmap"))));

        var rendered = 0;
        foreach (var ch in "AaQg&")
        {
            var gid = full.GetGlyphId(ch);
            var a = full.RenderGlyph(gid, 48f);
            var b = wrapped.RenderGlyph(gid, 48f);
            b.Width.ShouldBe(a.Width, $"width mismatch at '{ch}'");
            b.Height.ShouldBe(a.Height, $"height mismatch at '{ch}'");
            b.Alpha.ShouldBe(a.Alpha, $"pixel mismatch at '{ch}'");
            if (a.Width > 0) rendered++;
        }
        rendered.ShouldBeGreaterThan(0, "at least one glyph must have ink, or the test proves nothing");
    }

    [Fact]
    public void CffOnly_LoadsWithoutACmap()
    {
        var full = Full();
        var wrapped = OpenTypeFont.Load(Wrap(("CFF ", Table(full, "CFF "))));

        wrapped.NumGlyphs.ShouldBe(full.NumGlyphs);
        wrapped.GetGlyphIdByName("A").ShouldBe(full.GetGlyphIdByName("A"));
    }

    [Fact]
    public void TrueTypeWithoutHead_IsStillRefused()
    {
        // Only a CFF carries what 'head' and 'maxp' would say; a glyf font without them cannot be read.
        var full = Full();
        var bytes = Wrap(("cmap", Table(full, "cmap")));
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x00010000);
        Should.Throw<InvalidDataException>(() => OpenTypeFont.Load(bytes));
    }
}
