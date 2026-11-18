using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SharpAstro.Fonts.Color;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Puts an 'SVG ' table (and a 'CPAL', and anything else) into an existing font, in memory. The repo ships no font with
/// SVG glyphs, and the ones that exist are large and say little that a test wants said; a table written here says
/// exactly what the test is about, and the same bytes go to a browser as the oracle's font.
/// </summary>
internal static class SyntheticSvgFont
{
    /// <summary>
    /// The font with the given tables added, or put in place of the ones it has by those tags. A whole, valid SFNT:
    /// the directory in tag order, tables on four-byte boundaries, every checksum and the head adjustment recomputed,
    /// because a browser's font sanitiser reads it too.
    /// </summary>
    public static byte[] WithTables(byte[] font, params (string Tag, byte[] Data)[] tables)
    {
        var all = new SortedDictionary<uint, byte[]>();
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = font.AsSpan(12 + i * 16, 16);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]), length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            all[BinaryPrimitives.ReadUInt32BigEndian(record)] = font.AsSpan((int)offset, (int)length).ToArray();
        }
        foreach ((string tag, byte[] data) in tables)
        {
            all[BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(tag))] = data;
        }

        const uint head = 0x68656164;
        int size = 12 + all.Count * 16;
        foreach (byte[] data in all.Values) size += (data.Length + 3) & ~3;
        byte[] result = new byte[size];
        font.AsSpan(0, 4).CopyTo(result);
        int selector = (int)Math.Floor(Math.Log2(all.Count)), range = (1 << selector) * 16;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)all.Count);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)range);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10), (ushort)(all.Count * 16 - range));

        int at = 12 + all.Count * 16, index = 0, headAt = -1;
        foreach ((uint tag, byte[] data) in all)
        {
            data.CopyTo(result, at);
            if (tag == head)
            {
                headAt = at;
                result.AsSpan(at + 8, 4).Clear();       // checkSumAdjustment is zero while sums are taken
            }
            Span<byte> record = result.AsSpan(12 + index++ * 16, 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Sum(result.AsSpan(at, (data.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            at += (data.Length + 3) & ~3;
        }
        if (headAt >= 0) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headAt + 8), 0xB1B0AFBA - Sum(result));
        return result;

        static uint Sum(ReadOnlySpan<byte> bytes)
        {
            uint sum = 0;
            for (int i = 0; i + 4 <= bytes.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]);
            return sum;
        }
    }

    /// <summary>
    /// An 'SVG ' table from records in the order given. Records that hold the SAME array share one document in the
    /// table, which is how a real one covers ranges that are apart.
    /// </summary>
    public static byte[] SvgTable(params (ushort First, ushort Last, byte[] Document)[] records)
    {
        var documents = new List<byte[]>();
        foreach ((_, _, byte[] document) in records)
        {
            if (!documents.Exists(known => ReferenceEquals(known, document))) documents.Add(document);
        }
        int listSize = 2 + records.Length * 12;
        var offsets = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        int at = listSize;
        foreach (byte[] document in documents)
        {
            offsets[document] = at;
            at += document.Length;
        }

        byte[] table = new byte[10 + at];
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(2), 10);
        Span<byte> list = table.AsSpan(10);
        BinaryPrimitives.WriteUInt16BigEndian(list, (ushort)records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            Span<byte> record = list.Slice(2 + i * 12, 12);
            BinaryPrimitives.WriteUInt16BigEndian(record, records[i].First);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], records[i].Last);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], (uint)offsets[records[i].Document]);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)records[i].Document.Length);
        }
        foreach (byte[] document in documents) document.CopyTo(list[offsets[document]..]);
        return table;
    }

    /// <summary>A version 0 'CPAL' table: every palette the same length, colours stored B, G, R, A.</summary>
    public static byte[] CpalTable(params Rgba32[][] palettes)
    {
        int entries = palettes[0].Length, records = entries * palettes.Length, header = 12 + palettes.Length * 2;
        byte[] table = new byte[header + records * 4];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), (ushort)entries);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), (ushort)palettes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), (ushort)records);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), (uint)header);
        for (int p = 0; p < palettes.Length; p++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12 + p * 2), (ushort)(p * entries));
            for (int i = 0; i < entries; i++)
            {
                Rgba32 c = palettes[p][i];
                (table[header + (p * entries + i) * 4], table[header + (p * entries + i) * 4 + 1]) = (c.B, c.G);
                (table[header + (p * entries + i) * 4 + 2], table[header + (p * entries + i) * 4 + 3]) = (c.R, c.A);
            }
        }
        return table;
    }

    public static byte[] Utf8(string document) => Encoding.UTF8.GetBytes(document);

    /// <summary>The document as a font may also store it: gzip, deflate inside.</summary>
    public static byte[] Gzip(string document)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Utf8(document));
        }
        return buffer.ToArray();
    }
}
