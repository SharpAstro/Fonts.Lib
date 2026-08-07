using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Shx;

/// <summary>Which of the SHX layouts a file uses. The header text selects it.</summary>
public enum ShxFormat
{
    /// <summary>
    /// <c>AutoCAD-86 unifont 1.0</c> — a text font keyed by Unicode code point.
    /// The stock Western faces (<c>txt</c>, <c>romans</c>, <c>isocp</c>, …) are all unifont.
    /// </summary>
    Unifont,

    /// <summary>
    /// <c>AutoCAD-86 bigfont 1.0</c> — a text font keyed by a double-byte CJK code,
    /// with the lead-byte ranges of that encoding declared in the header.
    /// </summary>
    BigFont,
}

/// <summary>
/// Which set of glyph commands to run. SHX encodes vertical forms inside the
/// <em>same</em> glyph as the horizontal ones, gated on the <c>0x0E</c> command,
/// rather than in a separate glyph as <c>vmtx</c>/GSUB would.
/// </summary>
public enum ShxTextOrientation
{
    /// <summary>Horizontal text: every <c>0x0E</c>-marked command is skipped.</summary>
    Horizontal,

    /// <summary>Vertical text: <c>0x0E</c>-marked commands run, so <c>0x0E</c> is a no-op.</summary>
    Vertical,
}

/// <summary>
/// A loaded AutoCAD <c>.shx</c> shape font — the format DWG text styles use, and the
/// reason SHX text in a plotted PDF arrives as bare path geometry with no font object
/// and no <c>/ToUnicode</c> behind it.
///
/// <para>Kept separate from <see cref="OpenTypeFont"/> deliberately: SHX shares no
/// tables, no <c>cmap</c> and no SFNT structure with it, and unlike every other format
/// this library reads it is <b>stroked, not filled</b> — see <see cref="IsStroked"/>.</para>
///
/// <para><b>Shape libraries are rejected.</b> Files whose header says
/// <c>AutoCAD-86 shapes</c> (<c>simplex.shx</c>, <c>ACAD.SHX</c>, P&amp;ID and survey symbol
/// sets) are not text fonts — their records are addressed by shape number from a DWG, not
/// by character code — and <see cref="Load"/> throws
/// <see cref="NotSupportedException"/> for them rather than reading them with the wrong
/// layout. In a 4,428-file survey of stock and third-party faces they were the clear
/// majority (3,669 shape libraries against 170 unifont and 362 bigfont), so this is the
/// common case, not an edge case.</para>
///
/// <para>Immutable post-construction; safe for concurrent reads.</para>
/// </summary>
public sealed class ShxFont
{
    // Code -> raw record bytes (glyph name + opcode stream). Never mutated after
    // construction, so concurrent readers are safe.
    private readonly Dictionary<int, byte[]> _glyphs;

    /// <summary>How far into the file to look for the <c>0x1A</c> header terminator.</summary>
    private const int HeaderScanLimit = 40;

    /// <summary>Which layout this file uses.</summary>
    public ShxFormat Format { get; }

    /// <summary>
    /// The header line, terminator stripped — e.g. <c>AutoCAD-86 unifont 1.0</c>.
    /// Not always exactly 25 bytes: <c>shapes</c> headers are 24 and one surveyed
    /// bigfont said <c>AutoCAD-586</c>, which is why the terminator is scanned for
    /// rather than assumed at a fixed offset.
    /// </summary>
    public string Header { get; }

    /// <summary>
    /// The face name from the font-definition record. <b>Not</b> the file name, and the
    /// PDF records neither — font identity for extracted geometry can only be recovered
    /// by matching the geometry itself.
    /// </summary>
    public string Name { get; }

    /// <summary>Ascent in font units, from the font-definition record.</summary>
    public int Above { get; }

    /// <summary>Descent in font units, from the font-definition record.</summary>
    public int Below { get; }

    /// <summary>
    /// The <c>modes</c> byte. 0 or 2 in 513 of 520 surveyed faces; 2 means the face
    /// carries vertical forms.
    /// </summary>
    public int Modes { get; }

    /// <summary>True when the face has vertical variants gated on <c>0x0E</c>.</summary>
    public bool HasVerticalForms => Modes != 0;

    /// <summary>
    /// The em, as <see cref="Above"/> + <see cref="Below"/>. SHX has no
    /// <c>unitsPerEm</c> field, no <c>hmtx</c> and no kerning; this is the only
    /// scale the format states. May be 0 in a damaged face.
    /// </summary>
    public int UnitsPerEm => Above + Below;

    /// <summary>
    /// Always true. SHX glyph geometry is a <b>pen path with no width</b> — the width
    /// comes from the graphics state (the <c>w</c> operator in PDF), never from the font,
    /// and there is no filled counter: the bowl of an <c>O</c> is a stroked circle, not
    /// two contours. Consumers that want an area to fill must run the path through a
    /// stroker (<see cref="RenderGlyph"/> does); consumers that want geometry — text
    /// extraction, shape matching, hit-testing — should take the open path from
    /// <see cref="TryGetGlyph"/> directly.
    ///
    /// <para>Carried as per-face state rather than a constant so that it stays meaningful
    /// on a face — a common interface over "things that can emit a glyph into an
    /// <see cref="IGlyphSink"/>" would want to declare it — and so a later format whose
    /// answer differs has somewhere to put it.</para>
    /// </summary>
    public bool IsStroked { get; }

    /// <summary>
    /// Lead-byte ranges of the double-byte encoding; empty for
    /// <see cref="ShxFormat.Unifont"/>.
    ///
    /// <para>These identify the encoding <em>family</em> but not the codepage: a face
    /// declaring <c>0x81-0x9F, 0xE0-0xEA, 0xFD-0xFE</c> is Shift-JIS shaped and one
    /// declaring <c>0x80-0xFF</c> is Big5/GBK shaped, but the file never says which.
    /// Codes are therefore opaque at this boundary — the caller supplies the mapping to
    /// Unicode.</para>
    /// </summary>
    public ImmutableArray<(int Start, int End)> LeadByteRanges { get; }

    /// <summary>Every code this face defines, ascending.</summary>
    public ImmutableArray<int> Codes { get; }

    private ShxFont(ShxFormat format, string header, string name, int above, int below,
        int modes, ImmutableArray<(int Start, int End)> leadByteRanges,
        Dictionary<int, byte[]> glyphs)
    {
        Format = format;
        Header = header;
        Name = name;
        Above = above;
        Below = below;
        Modes = modes;
        IsStroked = true;
        LeadByteRanges = leadByteRanges;
        _glyphs = glyphs;
        var codes = glyphs.Keys.ToArray();
        Array.Sort(codes);
        Codes = ImmutableCollectionsMarshal.AsImmutableArray(codes);
    }

    /// <inheritdoc cref="Load"/>
    public static ShxFont LoadFromFile(string path) => Load(File.ReadAllBytes(path));

    /// <summary>Load an SHX font from raw bytes.</summary>
    /// <exception cref="NotSupportedException">
    /// The file is an <c>AutoCAD-86 shapes</c> symbol library rather than a text font.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file is not an SHX file, or its header fields do not fit inside it.
    /// </exception>
    public static ShxFont Load(ReadOnlySpan<byte> data)
    {
        var (header, offset) = ReadHeader(data);
        var lower = header.ToLowerInvariant();

        if (lower.Contains("shapes", StringComparison.Ordinal))
            throw new NotSupportedException(
                $"'{header}' is an AutoCAD shape library, not a text font. Its records are " +
                "addressed by shape number from a DWG rather than by character code, so there " +
                "is no character mapping to read; parsing one with the unifont layout yields " +
                "a handful of nonsense glyphs and then runs off the end of a record. " +
                "Shape-number lookup is not implemented.");

        if (lower.Contains("unifont", StringComparison.Ordinal))
            return LoadUnifont(data, header, offset);

        if (lower.Contains("bigfont", StringComparison.Ordinal))
            return LoadBigFont(data, header, offset);

        throw new InvalidDataException(
            $"Not a recognised SHX font: header '{header}' is none of unifont, bigfont or shapes.");
    }

    /// <summary>
    /// The header is ASCII terminated by <c>0x1A</c>. Its length is <em>not</em> fixed —
    /// 25 bytes for unifont and bigfont, 24 for shapes, 23 for one file using a bare
    /// <c>\n</c>, 26 for a face calling itself <c>AutoCAD-586</c> — so the terminator is
    /// scanned for rather than assumed.
    /// </summary>
    private static (string Text, int End) ReadHeader(ReadOnlySpan<byte> data)
    {
        var limit = Math.Min(data.Length, HeaderScanLimit);
        var mark = limit > 0 ? data[..limit].IndexOf((byte)0x1A) : -1;
        if (mark < 0)
            throw new InvalidDataException(
                "Not an SHX file: no 0x1A header terminator in the first 40 bytes.");
        var text = Encoding.ASCII.GetString(data[..mark]).TrimEnd('\r', '\n');
        return (text, mark + 1);
    }

    /// <summary>
    /// unifont: a count, the font-definition record, then the glyph records stored
    /// inline. 154 of 170 surveyed faces tile exactly to EOF; the other 16 carry a
    /// 48-byte ASCII GUID watermark appended by some authoring tool, which is why
    /// trailing bytes are ignored rather than treated as a parse error.
    /// </summary>
    private static ShxFont LoadUnifont(ReadOnlySpan<byte> data, string header, int offset)
    {
        if (offset + 6 > data.Length)
            throw new InvalidDataException("Truncated unifont header.");

        // Count includes the font-definition record.
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        var defLength = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;

        if (offset + defLength > data.Length)
            throw new InvalidDataException("unifont font-definition record runs past EOF.");
        var (name, above, below, modes) = ReadFontDefinition(data.Slice(offset, defLength));
        offset += defLength;

        var glyphs = new Dictionary<int, byte[]>();
        for (var n = 1u; n < count; n++)
        {
            // Bounds-checked rather than trusted: real faces contain truncated records,
            // and keeping what parsed beats throwing partway through a usable font.
            if (offset + 4 > data.Length) break;
            var code = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            offset += 4;
            if (offset + length > data.Length) break;
            glyphs[code] = data.Slice(offset, length).ToArray();
            offset += length;
        }

        return new ShxFont(ShxFormat.Unifont, header, name, above, below, modes,
            ImmutableArray<(int, int)>.Empty, glyphs);
    }

    /// <summary>
    /// bigfont: the lead-byte ranges, then an <b>index table</b> of
    /// <c>(uint16 code, uint16 length, uint32 offset)</c> entries pointing into a
    /// contiguous data area — <em>not</em> inline records as unifont uses. 358 of 362
    /// surveyed faces have the index abutting the data area byte-for-byte; the other 4
    /// are damaged, with entry offsets past EOF, so entries are range-checked and
    /// dropped individually.
    /// </summary>
    private static ShxFont LoadBigFont(ReadOnlySpan<byte> data, string header, int offset)
    {
        if (offset + 6 > data.Length)
            throw new InvalidDataException("Truncated bigfont header.");

        // First field is 8 in 350 of 362 surveyed faces and 0 in the other 12; its
        // purpose is unconfirmed and nothing here depends on it. The index entry size
        // is a fixed 8 bytes for both values.
        offset += 2;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var rangeCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;

        if (offset + rangeCount * 4 > data.Length)
            throw new InvalidDataException("bigfont lead-byte ranges run past EOF.");
        var ranges = ImmutableArray.CreateBuilder<(int Start, int End)>(rangeCount);
        for (var i = 0; i < rangeCount; i++)
        {
            var start = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var end = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            offset += 4;
            ranges.Add((start, end));
        }

        const int IndexEntrySize = 8;
        var name = string.Empty;
        int above = 0, below = 0, modes = 0;
        var glyphs = new Dictionary<int, byte[]>();

        for (var i = 0; i < count; i++)
        {
            var entry = offset + i * IndexEntrySize;
            if (entry + IndexEntrySize > data.Length) break;
            var code = BinaryPrimitives.ReadUInt16LittleEndian(data[entry..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[(entry + 2)..]);
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 4)..]);

            if (length == 0) continue;
            if (dataOffset > (uint)data.Length || dataOffset + length > (uint)data.Length)
                continue;
            var record = data.Slice((int)dataOffset, length);

            // Code 0 is the font definition, not a glyph.
            if (code == 0)
            {
                (name, above, below, modes) = ReadFontDefinition(record);
                continue;
            }
            glyphs[code] = record.ToArray();
        }

        return new ShxFont(ShxFormat.BigFont, header, name, above, below, modes,
            ranges.ToImmutable(), glyphs);
    }

    /// <summary>
    /// The font-definition record: a NUL-terminated face name, then <c>above</c>,
    /// <c>below</c> and <c>modes</c> as single bytes. Same shape in both formats.
    /// </summary>
    private static (string Name, int Above, int Below, int Modes) ReadFontDefinition(
        ReadOnlySpan<byte> record)
    {
        var nul = record.IndexOf((byte)0);
        if (nul < 0) return (string.Empty, 0, 0, 0);
        var name = Encoding.ASCII.GetString(record[..nul]);
        var tail = record[(nul + 1)..];
        var above = tail.Length > 0 ? tail[0] : 0;
        var below = tail.Length > 1 ? tail[1] : 0;
        var modes = tail.Length > 2 ? tail[2] : 0;
        return (name, above, below, modes);
    }

    /// <summary>True if this face defines <paramref name="code"/>.</summary>
    public bool HasGlyph(int code) => _glyphs.ContainsKey(code);

    /// <summary>
    /// True if <paramref name="b"/> begins a double-byte sequence per
    /// <see cref="LeadByteRanges"/>. Always false for unifont, which has no ranges.
    /// </summary>
    public bool IsLeadByte(int b)
    {
        foreach (var (start, end) in LeadByteRanges)
            if (b >= start && b <= end) return true;
        return false;
    }

    /// <summary>Raw record bytes for <paramref name="code"/> — glyph name then opcodes.</summary>
    internal bool TryGetRecord(int code, out byte[] record) => _glyphs.TryGetValue(code, out record!);

    /// <summary>
    /// Emit the glyph for <paramref name="code"/> into <paramref name="sink"/> as an
    /// <b>open path in font units</b>, baseline at y=0. Returns false if the face has no
    /// such code.
    ///
    /// <para><see cref="IGlyphSink.Close"/> is never called: the result is a pen path, not
    /// a closed contour, and a fill rasterizer given it directly produces recognisable but
    /// wrong glyphs — self-intersecting and with no weight. Use
    /// <see cref="TryGetStrokedOutline"/> or <see cref="RenderGlyph"/> for something
    /// fillable.</para>
    /// </summary>
    public bool TryGetGlyph(int code, IGlyphSink sink,
        ShxTextOrientation orientation = ShxTextOrientation.Horizontal)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!_glyphs.TryGetValue(code, out var record)) return false;
        ShxShapeInterpreter.Execute(this, record, sink, orientation);
        return true;
    }

    /// <summary>
    /// The advance width for <paramref name="code"/> in font units — the pen's X position
    /// at end-of-shape, which is the only advance SHX states. There is no <c>hmtx</c> and
    /// no kerning.
    /// </summary>
    public bool TryGetAdvance(int code, out float advance,
        ShxTextOrientation orientation = ShxTextOrientation.Horizontal)
    {
        advance = 0f;
        if (!_glyphs.TryGetValue(code, out var record)) return false;
        advance = ShxShapeInterpreter.Execute(this, record, sink: null, orientation);
        return true;
    }

    /// <summary>
    /// Stroke the glyph's pen path to a fillable closed outline of width
    /// <paramref name="strokeWidth"/> in font units, emitted to <paramref name="sink"/>.
    ///
    /// <para>The width is the caller's: it lives in the graphics state of whatever placed
    /// the text, not in the font. Round cap and round join are the defaults because
    /// AutoCAD plots SHX with a pen.</para>
    /// </summary>
    public bool TryGetStrokedOutline(int code, IGlyphSink sink, float strokeWidth,
        LineCap cap = LineCap.Round, LineJoin join = LineJoin.Round,
        ShxTextOrientation orientation = ShxTextOrientation.Horizontal)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!_glyphs.TryGetValue(code, out var record)) return false;
        OutlineStroker.Stroke(
            s => ShxShapeInterpreter.Execute(this, record, s, orientation),
            sink, strokeWidth, cap, join);
        return true;
    }

    /// <summary>
    /// Rasterize a glyph to an 8-bit grayscale alpha bitmap at
    /// <paramref name="pixelsPerEm"/>, stroking the pen path at
    /// <paramref name="strokeWidth"/> font units on the way. Returns
    /// <see cref="GlyphBitmap.Empty"/> for an unknown code or an unusable em.
    /// </summary>
    public GlyphBitmap RenderGlyph(int code, float pixelsPerEm, float strokeWidth,
        LineCap cap = LineCap.Round, LineJoin join = LineJoin.Round,
        ShxTextOrientation orientation = ShxTextOrientation.Horizontal,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
    {
        if (!_glyphs.TryGetValue(code, out var record)) return GlyphBitmap.Empty;
        var em = UnitsPerEm;
        if (em <= 0) return GlyphBitmap.Empty;
        return SmoothRasterizer.Rasterize(
            sink => OutlineStroker.Stroke(
                s => ShxShapeInterpreter.Execute(this, record, s, orientation),
                sink, strokeWidth, cap, join),
            pixelsPerEm, em, subSamples);
    }
}
