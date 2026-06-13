using System.Text;
using SharpAstro.Fonts.Tables.Cmap;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Regression: a malformed or subsetted cmap can map a codepoint to a glyph
/// index past the font's <see cref="OpenTypeFont.NumGlyphs"/> — e.g. a PDF
/// embedded subset that kept the original font's char-code-keyed cmap after the
/// referenced glyphs were dropped. There is no outline data for such an index,
/// so <see cref="OpenTypeFont"/> must clamp it to .notdef (0) at the lookup
/// boundary. Otherwise the bad gid flows into <c>DrawGlyph</c>, which throws
/// <see cref="ArgumentOutOfRangeException"/> — the unhandled crash hit while
/// pre-warming fonts for a real document (gid 81 into a 59-glyph subset).
/// </summary>
public class GlyphIdRangeTests
{
    private const ushort NumGlyphs   = 10;
    private const uint   OverMapCp   = 'A'; // cmap maps this past NumGlyphs
    private const uint   OverMapGid  = 81;
    private const uint   InRangeCp   = 'B'; // cmap maps this within range
    private const uint   InRangeGid  = 5;

    [Fact]
    public void GetGlyphId_OutOfRangeCmapEntry_ClampedToNotdef()
    {
        var font = OpenTypeFont.Load(BuildFontWithOverMappingCmap());
        font.NumGlyphs.ShouldBe(NumGlyphs);

        // The raw cmap subtable still over-maps — proves the test has teeth:
        // without the clamp this gid reaches DrawGlyph and throws.
        font.Cmap.ShouldNotBeNull()
            .PreferredUnicodeSubtable().ShouldNotBeNull()
            .GetGlyphId(OverMapCp).ShouldBe(OverMapGid);

        // Every public lookup path must clamp the out-of-range gid to .notdef.
        font.GetGlyphId(OverMapCp).ShouldBe(0u);
        font.GetGlyphId(OverMapCp, OverMapCp, GlyphMapHint.Auto).ShouldBe(0u);
        font.GetGlyphId(OverMapCp, OverMapCp, GlyphMapHint.Unicode).ShouldBe(0u);
        font.GetGlyphId(OverMapCp, OverMapCp, GlyphMapHint.EmbeddedSubset).ShouldBe(0u);
    }

    [Fact]
    public void GetGlyphId_InRangeCmapEntry_PassesThrough()
    {
        var font = OpenTypeFont.Load(BuildFontWithOverMappingCmap());
        // A legitimately-mapped, in-range gid is unaffected by the clamp.
        font.GetGlyphId(InRangeCp).ShouldBe(InRangeGid);
    }

    // --- Minimal synthetic SFNT: offset table + directory + head/maxp/cmap ---
    // Just enough for OpenTypeFont.Load (head + maxp required, cmap optional);
    // no glyf/loca/hmtx needed since the test only exercises cmap → gid mapping.
    private static byte[] BuildFontWithOverMappingCmap()
    {
        // cmap format 6 (trimmed array): firstCode='A', two contiguous entries.
        var sub = new List<byte>();
        U16(sub, 6);                 // format
        U16(sub, 0);                 // length placeholder (format 6 parse ignores it)
        U16(sub, 0);                 // language
        U16(sub, (int)OverMapCp);    // firstCode = 'A'
        U16(sub, 2);                 // entryCount: 'A', 'B'
        U16(sub, (int)OverMapGid);   // 'A' -> 81  (out of range)
        U16(sub, (int)InRangeGid);   // 'B' -> 5   (in range)
        sub[2] = (byte)(sub.Count >> 8); sub[3] = (byte)sub.Count; // backfill length

        // cmap header: one (Windows, Unicode BMP) encoding record → genuine Unicode.
        var cmap = new List<byte>();
        U16(cmap, 0);   // version
        U16(cmap, 1);   // numTables
        U16(cmap, 3);   // platformID = Windows
        U16(cmap, 1);   // encodingID = Unicode BMP
        U32(cmap, 12);  // subtable offset (header 4 + record 8)
        cmap.AddRange(sub);

        // head: 54 bytes; only unitsPerEm (offset 18) and indexToLocFormat
        // (offset 50, left 0) are read by HeadTable.Parse.
        var head = new byte[54];
        head[18] = 0x03; head[19] = 0xE8; // unitsPerEm = 1000

        // maxp v0.5 (CFF flavor, 6 bytes): version + numGlyphs.
        var maxp = new List<byte>();
        U32(maxp, 0x00005000); U16(maxp, NumGlyphs);

        var tables = new (string Tag, byte[] Data)[]
        {
            ("cmap", cmap.ToArray()),
            ("head", head),
            ("maxp", maxp.ToArray()),
        };

        var file = new List<byte>();
        U32(file, 0x00010000);    // sfntVersion (TrueType)
        U16(file, tables.Length); // numTables
        U16(file, 0); U16(file, 0); U16(file, 0); // searchRange/entrySelector/rangeShift (ignored)

        var dataOffset = 12 + tables.Length * 16;
        foreach (var (tag, data) in tables)
        {
            file.AddRange(Encoding.ASCII.GetBytes(tag)); // 4-byte tag
            U32(file, 0);                // checksum (not verified by the parser)
            U32(file, dataOffset);
            U32(file, data.Length);
            dataOffset += data.Length;
        }
        foreach (var (_, data) in tables) file.AddRange(data);
        return file.ToArray();

        static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        static void U32(List<byte> b, long v)
        { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
    }
}
