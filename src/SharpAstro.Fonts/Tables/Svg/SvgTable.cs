using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Svg;

/// <summary>
/// Parsed 'SVG ' table: which glyphs have an SVG description, and where each one's document lies.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/svg
///
/// <para>The table is a list of records, each a range of glyph ids and one SVG document (plain UTF-8 or gzip). A
/// document describes every glyph of its range, each as the element with the id <c>glyph&lt;id&gt;</c>, and several
/// records may name the same document. This class finds the document; it does not read it
/// (<see cref="Color.SvgGlyphRenderer"/> does, through <c>SharpAstro.Svg</c>).</para>
///
/// <para>Every offset and length is the font's word and is checked against the table: a record whose document does
/// not lie inside the table is dropped, and the glyphs it names have no SVG description.</para>
/// </summary>
public sealed class SvgTable
{
    private readonly ReadOnlyMemory<byte> _table;
    private readonly Record[] _records;

    private readonly record struct Record(ushort First, ushort Last, int Offset, int Length);

    private SvgTable(ReadOnlyMemory<byte> table, Record[] records)
    {
        _table = table;
        _records = records;
    }

    /// <summary>How many usable document records the table has. Several may share one document.</summary>
    public int RecordCount => _records.Length;

    /// <summary>
    /// Parses the header and the document list. The table's bytes are kept, not copied. Null when there is nothing
    /// usable in it: a version other than 0, a list that does not lie in the table, no record with a document.
    /// </summary>
    public static SvgTable? Parse(ReadOnlyMemory<byte> table)
    {
        ReadOnlySpan<byte> data = table.Span;
        if (data.Length < 10) return null;
        var header = new BigEndianReader(data);
        if (header.ReadUInt16() != 0) return null;
        uint listOffset = header.ReadUInt32();
        if (listOffset < 10 || listOffset > (uint)data.Length - 2) return null;

        var list = new BigEndianReader(data, (int)listOffset);
        int count = list.ReadUInt16();
        // What the list declares is bounded by what is there to hold it: twelve bytes a record.
        count = Math.Min(count, list.Remaining / 12);
        var records = new List<Record>(count);
        bool sorted = true;
        for (int i = 0; i < count; i++)
        {
            ushort first = list.ReadUInt16();
            ushort last = list.ReadUInt16();
            uint offset = list.ReadUInt32();
            uint length = list.ReadUInt32();
            // Offsets are from the start of the document list. In 64 bits, so that no sum can wrap.
            long start = (long)listOffset + offset;
            if (first > last || offset == 0 || length == 0 || start + length > data.Length) continue;
            sorted &= records.Count == 0 || first > records[^1].Last;
            records.Add(new Record(first, last, (int)start, (int)length));
        }
        if (records.Count == 0) return null;
        // The specification wants the records in order and apart. A table that is not in order is put in order (a
        // stable sort, so of two records that claim a glyph the one written first is found first).
        Record[] result = sorted ? [.. records] : [.. records.OrderBy(record => record.First)];
        return new SvgTable(table, result);
    }

    /// <summary>Whether a document is recorded for the glyph. The document may still not hold an element for it.</summary>
    public bool HasGlyph(uint glyphId) => Find(glyphId) >= 0;

    /// <summary>
    /// The SVG document recorded for a glyph, as it is stored: plain UTF-8, or gzip (first bytes 1F 8B). The glyph is
    /// the element with the id <c>glyph&lt;glyphId&gt;</c> in it. A slice of the font's own bytes.
    /// </summary>
    public bool TryGetDocument(uint glyphId, out ReadOnlyMemory<byte> document)
    {
        int index = Find(glyphId);
        if (index < 0)
        {
            document = default;
            return false;
        }
        document = _table.Slice(_records[index].Offset, _records[index].Length);
        return true;
    }

    private int Find(uint glyphId)
    {
        // The first record whose range holds the glyph. Ranges may overlap in a table that breaks the rules, so this
        // is the last record that starts at or before the glyph, and then back over any that start there too.
        int low = 0, high = _records.Length - 1, at = -1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (_records[middle].First <= glyphId)
            {
                at = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        int found = -1;
        for (int i = at; i >= 0; i--)
        {
            if (_records[i].Last >= glyphId) found = i;
            // In a table that keeps the rules the record before ends below this one's start, and the search is over.
            else if (i < at) break;
        }
        return found;
    }
}
