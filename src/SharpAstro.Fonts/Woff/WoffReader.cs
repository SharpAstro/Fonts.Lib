using System.Buffers.Binary;
using System.IO.Compression;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Woff;

/// <summary>
/// Reads WOFF1 (Web Open Font Format 1.0) files and reconstructs the
/// underlying SFNT/OpenType byte stream.
///
/// <para>Spec: https://www.w3.org/TR/WOFF/</para>
///
/// <para>WOFF wraps a standard SFNT font with per-table zlib compression.
/// <see cref="UnpackToSfnt"/> rebuilds the original SFNT byte array which can
/// be passed directly to <see cref="OpenTypeFont.Load(byte[])"/>.</para>
/// </summary>
public static class WoffReader
{
    // WOFF signature: 'wOFF' = 0x774F4646
    private const uint WoffSignature = 0x774F_4646u;

    // SFNT offset table is 12 bytes; each table directory entry is 16 bytes.
    private const int SfntOffsetTableSize = 12;
    private const int SfntTableRecordSize = 16;

    // WOFF header is 44 bytes (per spec).
    private const int WoffHeaderSize = 44;
    // Each WOFF table directory entry is 20 bytes.
    private const int WoffTableEntrySize = 20;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="data"/> starts with
    /// the WOFF1 signature bytes (<c>wOFF</c> = 0x774F4646).
    /// </summary>
    public static bool IsWoff(ReadOnlySpan<byte> data)
        => data.Length >= 4
           && BinaryPrimitives.ReadUInt32BigEndian(data) == WoffSignature;

    /// <summary>
    /// Unpack a WOFF1 byte stream into a raw SFNT byte array that can be
    /// passed to <see cref="OpenTypeFont.Load(byte[])"/>.
    /// </summary>
    /// <param name="woffData">The complete WOFF1 file contents.</param>
    /// <returns>
    /// A freshly-allocated <see cref="byte"/>[] containing a valid SFNT font.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the WOFF signature is missing or data is malformed.
    /// </exception>
    public static byte[] UnpackToSfnt(ReadOnlySpan<byte> woffData)
    {
        if (woffData.Length < WoffHeaderSize)
            throw new InvalidDataException("Data is too short to be a WOFF file.");

        var r = new BigEndianReader(woffData);

        // --- WOFF header (44 bytes) ---
        var signature     = r.ReadUInt32(); // 0x774F4646
        var flavor        = r.ReadUInt32(); // original SFNT version (sfntVersion)
        /*var woffLength  =*/ r.ReadUInt32(); // total WOFF file size (not used directly)
        var numTables     = r.ReadUInt16();
        /*var reserved    =*/ r.ReadUInt16(); // must be zero, we don't validate
        /*var totalSfnt   =*/ r.ReadUInt32(); // original SFNT size hint (we recalculate)
        /*var majorVer    =*/ r.ReadUInt16(); // font revision — not needed for reconstruction
        /*var minorVer    =*/ r.ReadUInt16();
        /*var metaOffset  =*/ r.ReadUInt32(); // metadata block — we ignore it
        /*var metaLength  =*/ r.ReadUInt32();
        /*var metaOrig    =*/ r.ReadUInt32();
        /*var privOffset  =*/ r.ReadUInt32(); // private data block — we ignore it
        /*var privLength  =*/ r.ReadUInt32();

        if (signature != WoffSignature)
            throw new InvalidDataException(
                $"Invalid WOFF signature: expected 0x{WoffSignature:X8}, got 0x{signature:X8}.");

        // --- Parse WOFF table directory (numTables × 20 bytes) ---
        // Each entry: tag(4) + offset(4) + compLength(4) + origLength(4) + origChecksum(4)
        var entries = new WoffTableEntry[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var tag          = r.ReadUInt32();
            var offset       = r.ReadUInt32();
            var compLength   = r.ReadUInt32();
            var origLength   = r.ReadUInt32();
            var origChecksum = r.ReadUInt32();
            entries[i] = new WoffTableEntry(tag, offset, compLength, origLength, origChecksum);
        }

        // SFNT table entries must be sorted by tag (ascending) per the OpenType spec.
        // WOFF tables are required to be in ascending tag order as well (WOFF spec §4),
        // but we sort explicitly to be safe with non-conformant inputs.
        Array.Sort(entries, static (a, b) => a.Tag.CompareTo(b.Tag));

        // --- Calculate the reconstructed SFNT size ---
        // SFNT layout: offset table (12) + table records (numTables * 16) + table data
        // Table data for each table is padded to a 4-byte boundary.
        uint sfntDataSize = 0;
        foreach (var entry in entries)
            sfntDataSize += Align4(entry.OrigLength);

        var sfntSize = SfntOffsetTableSize + numTables * SfntTableRecordSize + (int)sfntDataSize;
        var sfnt = new byte[sfntSize];

        // --- Write SFNT offset table (12 bytes) ---
        // flavor = sfntVersion, numTables, searchRange, entrySelector, rangeShift
        var searchRange   = (ushort)(LargestPowerOfTwoLeq(numTables) * 16);
        var entrySelector = (ushort)Log2Floor(LargestPowerOfTwoLeq(numTables));
        var rangeShift    = (ushort)(numTables * 16 - searchRange);

        var w = 0; // write cursor into sfnt[]
        BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(w), flavor);        w += 4;
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(w), numTables);     w += 2;
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(w), searchRange);   w += 2;
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(w), entrySelector); w += 2;
        BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(w), rangeShift);    w += 2;

        // Table data starts right after all table records.
        var dataStart = SfntOffsetTableSize + numTables * SfntTableRecordSize;
        var dataWrite = dataStart; // cursor for writing decompressed table data

        // We need to write table records and table data in two passes:
        // first determine each table's SFNT offset (sequential), then write
        // the records, then the data. We collect SFNT offsets here.
        var sfntOffsets = new uint[numTables];
        var runningOffset = (uint)dataStart;
        for (var i = 0; i < numTables; i++)
        {
            sfntOffsets[i] = runningOffset;
            runningOffset += Align4(entries[i].OrigLength);
        }

        // --- Write SFNT table directory records (numTables × 16 bytes) ---
        for (var i = 0; i < numTables; i++)
        {
            var entry = entries[i];
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(w), entry.Tag);           w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(w), entry.OrigChecksum);  w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(w), sfntOffsets[i]);      w += 4;
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(w), entry.OrigLength);    w += 4;
        }

        // --- Decompress / copy table data ---
        for (var i = 0; i < numTables; i++)
        {
            var entry = entries[i];
            var compressedSlice = woffData.Slice((int)entry.Offset, (int)entry.CompLength);
            var destSlice = sfnt.AsSpan(dataWrite, (int)entry.OrigLength);

            if (entry.CompLength < entry.OrigLength)
            {
                // Table is zlib-compressed. ZLibStream expects the raw zlib stream
                // (2-byte header + deflate data + adler32 checksum).
                DecompressZlib(compressedSlice, destSlice);
            }
            else
            {
                // Table is stored uncompressed (compLength == origLength).
                compressedSlice.CopyTo(destSlice);
            }

            // Advance by the padded size; padding bytes remain 0 (array is zero-initialized).
            dataWrite += (int)Align4(entry.OrigLength);
        }

        return sfnt;
    }

    /// <summary>
    /// Load an <see cref="OpenTypeFont"/> directly from WOFF1 data.
    /// </summary>
    /// <param name="woffData">
    /// The complete WOFF1 file as <see cref="ReadOnlyMemory{Byte}"/>.
    /// </param>
    public static OpenTypeFont Load(ReadOnlyMemory<byte> woffData)
    {
        var sfnt = UnpackToSfnt(woffData.Span);
        return OpenTypeFont.Load(sfnt);
    }

    /// <summary>
    /// Load an <see cref="OpenTypeFont"/> from a WOFF1 file on disk.
    /// </summary>
    /// <param name="path">Path to the <c>.woff</c> file.</param>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));

    // --- Private helpers ---

    /// <summary>
    /// Decompress a raw zlib stream (RFC 1950) into <paramref name="dest"/>.
    /// Uses <see cref="ZLibStream"/> which handles the zlib header/trailer.
    /// </summary>
    private static void DecompressZlib(ReadOnlySpan<byte> compressed, Span<byte> dest)
    {
        // ZLibStream needs a Stream; wrap the span in a MemoryStream via a
        // temporary byte array copy (unavoidable — Stream.Read requires Memory<T>
        // and we have only a Span from a larger buffer).
        var compressedArray = compressed.ToArray();
        using var input = new MemoryStream(compressedArray, writable: false);
        using var zlib  = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);

        var written = 0;
        while (written < dest.Length)
        {
            // Read into a temporary buffer then copy into the span, because
            // ZLibStream.Read does not accept Span<byte> directly on all targets.
            var remaining = dest.Length - written;
            var buf = new byte[Math.Min(remaining, 81920)]; // 80 KB chunks
            var read = zlib.Read(buf, 0, buf.Length);
            if (read == 0) break;
            buf.AsSpan(0, read).CopyTo(dest[written..]);
            written += read;
        }
    }

    /// <summary>Round <paramref name="v"/> up to the next multiple of 4.</summary>
    private static uint Align4(uint v) => (v + 3u) & ~3u;

    /// <summary>Largest power of two ≤ <paramref name="n"/> (returns 1 when n = 0).</summary>
    private static int LargestPowerOfTwoLeq(int n)
    {
        if (n <= 0) return 1;
        var p = 1;
        while (p * 2 <= n) p *= 2;
        return p;
    }

    /// <summary>Floor of log2 of <paramref name="n"/> (0 for n ≤ 1).</summary>
    private static int Log2Floor(int n)
    {
        var result = 0;
        while (n > 1) { n >>= 1; result++; }
        return result;
    }

    /// <summary>
    /// One entry in the WOFF table directory.
    /// Fields match the WOFF spec §4 table directory layout.
    /// </summary>
    private readonly struct WoffTableEntry(
        uint tag, uint offset, uint compLength, uint origLength, uint origChecksum)
    {
        /// <summary>4-byte table tag, stored as big-endian uint32.</summary>
        public readonly uint Tag = tag;
        /// <summary>Offset of this table's data within the WOFF file.</summary>
        public readonly uint Offset = offset;
        /// <summary>Length of the compressed (or stored) data in the WOFF file.</summary>
        public readonly uint CompLength = compLength;
        /// <summary>Uncompressed table length (== CompLength when stored uncompressed).</summary>
        public readonly uint OrigLength = origLength;
        /// <summary>Checksum of the uncompressed table (copied verbatim into the SFNT directory).</summary>
        public readonly uint OrigChecksum = origChecksum;
    }
}
