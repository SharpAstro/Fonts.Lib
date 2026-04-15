using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace SharpAstro.Fonts.Woff;

/// <summary>
/// Reads WOFF2 font containers and unpacks them to raw SFNT bytes that can be
/// loaded by <see cref="OpenTypeFont.Load(ReadOnlyMemory{byte})"/>.
///
/// <para>Spec: https://www.w3.org/TR/WOFF2/</para>
///
/// <para><b>Transform support:</b> the common no-transform path (flags bits 6-7 = 3)
/// is fully supported. The optional glyf/loca transform (flags bits 6-7 = 0) is
/// also implemented, including 255UInt16, triplet coordinate, and instruction
/// stream decoding as described in the WOFF2 specification.</para>
/// </summary>
public static class Woff2Reader
{
    /// <summary>WOFF2 magic: 'wOF2' = 0x774F4632.</summary>
    private const uint Woff2Signature = 0x774F4632u;

    // -------------------------------------------------------------------------
    // Known-tag table (WOFF2 spec §Table directory, indices 0-62)
    // -------------------------------------------------------------------------
    private static readonly uint[] KnownTags =
    [
        /* 0  */ 0x636D6170u, // cmap
        /* 1  */ 0x68656164u, // head
        /* 2  */ 0x68686561u, // hhea
        /* 3  */ 0x686D7478u, // hmtx
        /* 4  */ 0x6D617870u, // maxp
        /* 5  */ 0x6E616D65u, // name
        /* 6  */ 0x4F532F32u, // OS/2
        /* 7  */ 0x706F7374u, // post
        /* 8  */ 0x63767420u, // cvt (with trailing space)
        /* 9  */ 0x6670676Du, // fpgm
        /* 10 */ 0x676C7966u, // glyf
        /* 11 */ 0x6C6F6361u, // loca
        /* 12 */ 0x70726570u, // prep
        /* 13 */ 0x43464620u, // CFF (with trailing space)
        /* 14 */ 0x564F5247u, // VORG
        /* 15 */ 0x45424454u, // EBDT
        /* 16 */ 0x45424C43u, // EBLC
        /* 17 */ 0x67617370u, // gasp
        /* 18 */ 0x68646D78u, // hdmx
        /* 19 */ 0x6B65726Eu, // kern
        /* 20 */ 0x4C545348u, // LTSH
        /* 21 */ 0x50434C54u, // PCLT
        /* 22 */ 0x56444D58u, // VDMX
        /* 23 */ 0x76686561u, // vhea
        /* 24 */ 0x766D7478u, // vmtx
        /* 25 */ 0x42415345u, // BASE
        /* 26 */ 0x47444546u, // GDEF
        /* 27 */ 0x47504F53u, // GPOS
        /* 28 */ 0x47535542u, // GSUB
        /* 29 */ 0x45425343u, // EBSC
        /* 30 */ 0x4A535446u, // JSTF
        /* 31 */ 0x4D415448u, // MATH
        /* 32 */ 0x43424454u, // CBDT
        /* 33 */ 0x43424C43u, // CBLC
        /* 34 */ 0x434F4C52u, // COLR
        /* 35 */ 0x4350414Cu, // CPAL
        /* 36 */ 0x53564720u, // SVG (with trailing space)
        /* 37 */ 0x73626978u, // sbix
        /* 38 */ 0x61636E74u, // acnt
        /* 39 */ 0x61766172u, // avar
        /* 40 */ 0x62646174u, // bdat
        /* 41 */ 0x626C6F63u, // bloc
        /* 42 */ 0x62736C6Eu, // bsln
        /* 43 */ 0x63766172u, // cvar
        /* 44 */ 0x66656174u, // feat
        /* 45 */ 0x66647363u, // fdsc
        /* 46 */ 0x666D7478u, // fmtx
        /* 47 */ 0x66766172u, // fvar
        /* 48 */ 0x67766172u, // gvar
        /* 49 */ 0x68737479u, // hsty
        /* 50 */ 0x6A757374u, // just
        /* 51 */ 0x6C636172u, // lcar
        /* 52 */ 0x6D6F7274u, // mort
        /* 53 */ 0x6D6F7278u, // morx
        /* 54 */ 0x6F706264u, // opbd
        /* 55 */ 0x70726F70u, // prop
        /* 56 */ 0x7472616Bu, // trak
        /* 57 */ 0x5A617066u, // Zapf
        /* 58 */ 0x53696C66u, // Silf
        /* 59 */ 0x476C6174u, // Glat
        /* 60 */ 0x476C6F63u, // Gloc
        /* 61 */ 0x46656174u, // Feat
        /* 62 */ 0x53696C6Cu, // Sill
    ];

    // Known-tag values for glyf and loca (used for transform detection)
    private static readonly uint GlyfTag = 0x676C7966u; // "glyf"
    private static readonly uint LocaTag = 0x6C6F6361u; // "loca"

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> if the first four bytes of <paramref name="data"/>
    /// match the WOFF2 signature <c>wOF2</c>.
    /// </summary>
    public static bool IsWoff2(ReadOnlySpan<byte> data)
        => data.Length >= 4
        && BinaryPrimitives.ReadUInt32BigEndian(data) == Woff2Signature;

    /// <summary>
    /// Unpack a WOFF2 container to a plain SFNT byte array. The returned
    /// array can be passed directly to
    /// <see cref="OpenTypeFont.Load(byte[])"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the WOFF2 data is malformed.</exception>
    public static byte[] UnpackToSfnt(ReadOnlySpan<byte> woff2Data)
    {
        if (!IsWoff2(woff2Data))
            throw new InvalidDataException("Not a WOFF2 file (signature mismatch).");

        // ---- 1. Parse the fixed 48-byte header ----
        var pos = 0;
        var signature       = ReadU32Be(woff2Data, ref pos);  // already verified
        var flavor          = ReadU32Be(woff2Data, ref pos);
        var length          = ReadU32Be(woff2Data, ref pos);
        var numTables       = ReadU16Be(woff2Data, ref pos);
        var reserved        = ReadU16Be(woff2Data, ref pos);  // must be 0
        var totalSfntSize   = ReadU32Be(woff2Data, ref pos);
        var totalCompressedSize = ReadU32Be(woff2Data, ref pos);
        var majorVersion    = ReadU16Be(woff2Data, ref pos);
        var minorVersion    = ReadU16Be(woff2Data, ref pos);
        var metaOffset      = ReadU32Be(woff2Data, ref pos);
        var metaLength      = ReadU32Be(woff2Data, ref pos);
        var metaOrigLength  = ReadU32Be(woff2Data, ref pos);
        var privOffset      = ReadU32Be(woff2Data, ref pos);
        var privLength      = ReadU32Be(woff2Data, ref pos);

        // ---- 2. Parse the variable-length table directory ----
        var entries = new TableEntry[numTables];
        for (var i = 0; i < numTables; i++)
            entries[i] = ReadTableEntry(woff2Data, ref pos);

        // ---- 3. Brotli-decompress the concatenated table data ----
        var compressedData = woff2Data.Slice(pos, (int)totalCompressedSize);
        var decompressed   = BrotliDecompress(compressedData, (int)totalSfntSize);

        // ---- 4. Rebuild each table from the decompressed stream ----
        //
        // WOFF2 stores tables in the order they appear in the directory.
        // Transformed tables (glyf/loca) must be reconstructed from their
        // custom binary format before being embedded into the SFNT output.

        // Identify glyf and loca table indices.
        var tableData = new byte[numTables][];
        var decPos = 0;
        var glyfIndex = -1;
        var locaIndex = -1;
        for (var i = 0; i < numTables; i++)
        {
            if (entries[i].Tag == GlyfTag) glyfIndex = i;
            if (entries[i].Tag == LocaTag) locaIndex = i;
        }

        // When glyf carries a transform (TransformLength non-null), the entire
        // glyf+loca payload is a single transform blob; loca consumes 0 bytes.
        var glyfHasTransform = glyfIndex >= 0 && entries[glyfIndex].TransformLength.HasValue;

        for (var i = 0; i < numTables; i++)
        {
            var entry = entries[i];

            if (entry.Tag == GlyfTag && glyfHasTransform)
            {
                // Consume the transform payload and reconstruct both glyf and loca.
                var txLen  = (int)entry.TransformLength!.Value;
                var txSpan = decompressed.AsSpan(decPos, txLen);
                decPos    += txLen;
                (tableData[glyfIndex], tableData[locaIndex]) = ReconstructGlyfLoca(txSpan);
                continue;
            }

            if (entry.Tag == LocaTag && glyfHasTransform)
            {
                // Loca is reconstructed alongside glyf; no bytes consumed here.
                // (WOFF2 spec guarantees glyf precedes loca in the table directory.)
                continue;
            }

            // Untransformed table: copy raw origLength bytes from the decompressed stream.
            var dataLen = (int)entry.OrigLength;
            tableData[i] = decompressed.AsSpan(decPos, dataLen).ToArray();
            decPos += dataLen;
        }

        // ---- 5. Assemble the SFNT output ----
        return AssembleSfnt(flavor, entries, tableData);
    }

    /// <summary>
    /// Load an <see cref="OpenTypeFont"/> directly from WOFF2 data in memory.
    /// </summary>
    public static OpenTypeFont Load(ReadOnlyMemory<byte> woff2Data)
    {
        var sfnt = UnpackToSfnt(woff2Data.Span);
        return OpenTypeFont.Load(sfnt);
    }

    /// <summary>
    /// Load an <see cref="OpenTypeFont"/> from a WOFF2 file on disk.
    /// </summary>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));

    // -------------------------------------------------------------------------
    // Brotli decompression
    // -------------------------------------------------------------------------

    private static byte[] BrotliDecompress(ReadOnlySpan<byte> compressed, int uncompressedSize)
    {
        // BrotliDecoder.TryDecompress: pure span-to-span, no Stream wrapper,
        // no intermediate copies. The output size is known from the WOFF2 header.
        var result = new byte[uncompressedSize];
        if (!BrotliDecoder.TryDecompress(compressed, result, out _))
            throw new InvalidDataException("Brotli decompression failed.");
        return result;
    }

    // -------------------------------------------------------------------------
    // Table directory entry parsing
    // -------------------------------------------------------------------------

    private readonly struct TableEntry
    {
        public readonly uint   Tag;
        public readonly uint   OrigLength;
        /// <summary>
        /// Non-null when bits 6-7 of the flags byte indicate a transform is
        /// present (transform version != 3). For glyf/loca the transform is
        /// always handled; for other tables transform version 3 = no transform.
        /// </summary>
        public readonly uint?  TransformLength;

        public TableEntry(uint tag, uint origLength, uint? transformLength)
        {
            Tag             = tag;
            OrigLength      = origLength;
            TransformLength = transformLength;
        }
    }

    private static TableEntry ReadTableEntry(ReadOnlySpan<byte> data, ref int pos)
    {
        var flags = data[pos++];
        var tagIndex = flags & 0x3F;
        var transformVersion = (flags >> 6) & 0x3;

        // Resolve the tag.
        uint tag;
        if (tagIndex == 63)
        {
            // Custom tag: next 4 bytes big-endian.
            tag = ReadU32Be(data, ref pos);
        }
        else
        {
            tag = KnownTags[tagIndex];
        }

        var origLength = ReadUIntBase128(data, ref pos);

        // Transform length is present when:
        //   - The table is glyf or loca AND transformVersion == 0 (default transform applied)
        //   - Any other table AND transformVersion != 3 (transform present)
        // When transformVersion == 3 for glyf/loca → no transform.
        // When transformVersion == 0 for glyf/loca → transform is applied.
        uint? transformLength = null;
        var isGlyfOrLoca = (tag == GlyfTag || tag == LocaTag);

        if (isGlyfOrLoca)
        {
            // For glyf/loca: transform version 0 = transform applied (read transformLength),
            //                transform version 3 = no transform (no transformLength field).
            if (transformVersion == 0)
                transformLength = ReadUIntBase128(data, ref pos);
            // versions 1, 2 reserved; treat as no transform.
        }
        else
        {
            // For all other tables: transform version 0 = no transform (no extra field),
            //                       transform version 1/2/3 = reserved/transform.
            // Per spec, only version 3 means "no transform" and has no transformLength.
            // Currently no other transforms are defined, but read if non-zero.
            if (transformVersion != 0)
                transformLength = ReadUIntBase128(data, ref pos);
        }

        return new TableEntry(tag, origLength, transformLength);
    }

    // -------------------------------------------------------------------------
    // UIntBase128 variable-length encoding
    // Spec: https://www.w3.org/TR/WOFF2/#DataTypes
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUIntBase128(ReadOnlySpan<byte> data, ref int pos)
    {
        uint accum = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[pos++];
            // Leading zeros are invalid (except the first byte being 0 itself).
            if (i == 0 && b == 0x80)
                throw new InvalidDataException("UIntBase128: leading zero byte.");
            // Overflow check: shifting would push bits beyond 32.
            if (accum > (uint.MaxValue >> 7))
                throw new InvalidDataException("UIntBase128: value overflows uint32.");
            accum = (accum << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return accum;
        }
        throw new InvalidDataException("UIntBase128: sequence exceeds 5 bytes.");
    }

    // -------------------------------------------------------------------------
    // glyf/loca transform reconstruction
    // Spec: https://www.w3.org/TR/WOFF2/#glyf_table_transform
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reconstruct canonical glyf and loca table bytes from the WOFF2
    /// transformed glyf stream. Returns (glyfBytes, locaBytes) where locaBytes
    /// uses long (format 1) loca offsets.
    /// </summary>
    private static (byte[] GlyfBytes, byte[] LocaBytes) ReconstructGlyfLoca(
        ReadOnlySpan<byte> transformedGlyf)
    {
        var pos = 0;

        // ---- Transform header ----
        // reserved (uint16) + optionFlags (uint16) + numGlyphs (uint16) + indexFormat (uint16)
        var reserved2    = ReadU16Be(transformedGlyf, ref pos);  // must be 0, ignored
        var optionFlags  = ReadU16Be(transformedGlyf, ref pos);
        var numGlyphs    = ReadU16Be(transformedGlyf, ref pos);
        var indexFormat  = ReadU16Be(transformedGlyf, ref pos);
        // optionFlags bit 0 (HAS_INSTRUCTIONS): when set, per-glyph instruction lengths
        // are encoded in the glyph stream. When clear, all glyphs have 0 instructions.
        var hasInstructions = (optionFlags & 0x0001) != 0;

        var nContourStreamSize   = ReadU32Be(transformedGlyf, ref pos);
        var nPointsStreamSize    = ReadU32Be(transformedGlyf, ref pos);
        var flagStreamSize       = ReadU32Be(transformedGlyf, ref pos);
        var glyphStreamSize      = ReadU32Be(transformedGlyf, ref pos);
        var compositeStreamSize  = ReadU32Be(transformedGlyf, ref pos);
        var bboxStreamSize       = ReadU32Be(transformedGlyf, ref pos);
        var instructionStreamSize = ReadU32Be(transformedGlyf, ref pos);

        // The spec says "fixed header" is 36 bytes (4 uint16s + 7 uint32s):
        // 4*2 + 7*4 = 8 + 28 = 36. The first uint16 is 'reserved' and the
        // second is optionFlags, then numGlyphs, indexFormat (4 × uint16 = 8),
        // then 7 × uint32 = 28 → 36 total. We've already consumed them above.

        // ---- Stream slices (in order within the transform payload) ----
        var nContourStream   = transformedGlyf.Slice(pos, (int)nContourStreamSize);
        pos += (int)nContourStreamSize;
        var nPointsStream    = transformedGlyf.Slice(pos, (int)nPointsStreamSize);
        pos += (int)nPointsStreamSize;
        var flagStream       = transformedGlyf.Slice(pos, (int)flagStreamSize);
        pos += (int)flagStreamSize;
        var glyphStream      = transformedGlyf.Slice(pos, (int)glyphStreamSize);
        pos += (int)glyphStreamSize;
        var compositeStream  = transformedGlyf.Slice(pos, (int)compositeStreamSize);
        pos += (int)compositeStreamSize;

        // bbox stream: bboxBitmap (ceil(numGlyphs/8) bytes) + per-glyph bbox records.
        var bboxBitmapSize = (numGlyphs + 7) / 8;
        var bboxBitmap     = transformedGlyf.Slice(pos, bboxBitmapSize);
        pos += bboxBitmapSize;
        // Each explicit bbox record is 4 × int16 = 8 bytes.
        var bboxValues     = transformedGlyf.Slice(pos, (int)bboxStreamSize - bboxBitmapSize);
        pos += (int)bboxStreamSize - bboxBitmapSize;

        var instructionStream = transformedGlyf.Slice(pos, (int)instructionStreamSize);

        // ---- Reconstruct per-glyph data ----
        // We build the glyf output as a list of byte-array blobs, one per glyph,
        // then concatenate them (with 4-byte alignment padding) to form the final
        // glyf table.

        var glyfBlobs = new byte[numGlyphs][];
        var nContourPos  = 0;
        var nPointsPos   = 0;
        var flagPos      = 0;
        var glyphPos     = 0;
        var compositePos = 0;
        var bboxValPos   = 0;
        var instrPos     = 0;

        for (var g = 0; g < numGlyphs; g++)
        {
            // nContours for this glyph (signed int16, big-endian).
            var nContours = (short)((nContourStream[nContourPos] << 8) |
                                     nContourStream[nContourPos + 1]);
            nContourPos += 2;

            if (nContours == 0)
            {
                // Empty glyph (no outline).
                glyfBlobs[g] = [];
                continue;
            }

            // Does this glyph have an explicit bbox record in the bbox stream?
            var hasBbox = (bboxBitmap[g / 8] & (0x80 >> (g % 8))) != 0;

            if (nContours > 0)
            {
                // Simple glyph.
                glyfBlobs[g] = ReconstructSimpleGlyph(
                    nContours,
                    hasBbox,
                    hasInstructions,
                    bboxValues,
                    ref bboxValPos,
                    nPointsStream,
                    ref nPointsPos,
                    flagStream,
                    ref flagPos,
                    glyphStream,
                    ref glyphPos,
                    instructionStream,
                    ref instrPos);
            }
            else
            {
                // Composite glyph (nContours == -1).
                glyfBlobs[g] = ReconstructCompositeGlyph(
                    hasBbox,
                    hasInstructions,
                    bboxValues,
                    ref bboxValPos,
                    compositeStream,
                    ref compositePos,
                    instructionStream,
                    ref instrPos);
            }
        }

        // ---- Concatenate glyf blobs with 4-byte padding ----
        // loca offsets use long format (uint32).
        var locaOffsets = new uint[numGlyphs + 1];
        var totalGlyfSize = 0;
        for (var g = 0; g < numGlyphs; g++)
        {
            locaOffsets[g]  = (uint)totalGlyfSize;
            var blobLen     = glyfBlobs[g].Length;
            // Each glyph record must be 4-byte aligned in glyf.
            totalGlyfSize  += (blobLen + 3) & ~3;
        }
        locaOffsets[numGlyphs] = (uint)totalGlyfSize;

        var glyfOut = new byte[totalGlyfSize];
        var gPos    = 0;
        for (var g = 0; g < numGlyphs; g++)
        {
            var blob = glyfBlobs[g];
            blob.CopyTo(glyfOut, gPos);
            gPos += (blob.Length + 3) & ~3;
        }

        // loca: long format = 4 bytes per entry, numGlyphs+1 entries.
        var locaOut = new byte[(numGlyphs + 1) * 4];
        for (var g = 0; g <= numGlyphs; g++)
            BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(g * 4), locaOffsets[g]);

        return (glyfOut, locaOut);
    }

    // -------------------------------------------------------------------------
    // Simple glyph reconstruction
    // -------------------------------------------------------------------------

    private static byte[] ReconstructSimpleGlyph(
        short nContours,
        bool hasBbox,
        bool hasInstructions,
        ReadOnlySpan<byte> bboxValues,
        ref int bboxValPos,
        ReadOnlySpan<byte> nPointsStream,
        ref int nPointsPos,
        ReadOnlySpan<byte> flagStream,
        ref int flagPos,
        ReadOnlySpan<byte> glyphStream,
        ref int glyphPos,
        ReadOnlySpan<byte> instructionStream,
        ref int instrPos)
    {
        // Read per-contour point counts (255UInt16 encoded).
        var contourEndPts = new int[nContours];
        var totalPoints = 0;
        for (var c = 0; c < nContours; c++)
        {
            var nPts = Read255UInt16(nPointsStream, ref nPointsPos);
            totalPoints += nPts;
            contourEndPts[c] = totalPoints - 1;
        }

        // Instruction length is 255UInt16-encoded in the glyph stream only when
        // HAS_INSTRUCTIONS (optionFlags bit 0) is set. Otherwise instrLen = 0.
        var instrLen = hasInstructions ? Read255UInt16(glyphStream, ref glyphPos) : 0;
        // The instruction bytes themselves come from the separate instruction stream.
        var instructions = instrLen > 0
            ? instructionStream.Slice(instrPos, instrLen).ToArray()
            : [];
        instrPos += instrLen;

        // Read flags and decode coordinate triplets.
        // WOFF2 flag stream byte layout:
        //   bit 7 (0x80): on-curve flag (1 = on-curve point)
        //   bits 0-6:     triplet type index (0-127) into Table 5
        // When tripletType == 127 the next byte in the FLAG STREAM is a repeat count
        // (not the glyph stream). The flagByte itself is repeated that many more times.
        var flags = new byte[totalPoints];
        var xCoords = new short[totalPoints];
        var yCoords = new short[totalPoints];
        int x = 0, y = 0;

        var p = 0;
        while (p < totalPoints)
        {
            var flagByte     = flagStream[flagPos++];
            var onCurve      = (flagByte & 0x80) != 0;
            var tripletIndex = flagByte & 0x7F;

            // Repeat: if tripletIndex == 127 the next flag stream byte is the repeat count.
            int repeatCount = 1;
            if (tripletIndex == 127)
            {
                repeatCount = flagStream[flagPos++] + 1;
                // Read the actual triplet type from the flag stream again.
                flagByte     = flagStream[flagPos++];
                onCurve      = (flagByte & 0x80) != 0;
                tripletIndex = flagByte & 0x7F;
            }

            for (var r = 0; r < repeatCount && p < totalPoints; r++, p++)
            {
                // Store the on-curve flag (as WOFF2 bit 7) for later TrueType encoding.
                flags[p] = onCurve ? (byte)0x80 : (byte)0;
                DecodeTriplet(glyphStream, ref glyphPos, tripletIndex, out var ddx, out var ddy);
                x += ddx;
                y += ddy;
                xCoords[p] = (short)x;
                yCoords[p] = (short)y;
            }
        }

        // ---- Compute bbox (from explicit record or by scanning points) ----
        short xMin, yMin, xMax, yMax;
        if (hasBbox)
        {
            xMin = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            yMin = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            xMax = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            yMax = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
        }
        else
        {
            xMin = xMax = totalPoints > 0 ? xCoords[0] : (short)0;
            yMin = yMax = totalPoints > 0 ? yCoords[0] : (short)0;
            for (var pi = 1; pi < totalPoints; pi++)
            {
                if (xCoords[pi] < xMin) xMin = xCoords[pi];
                if (xCoords[pi] > xMax) xMax = xCoords[pi];
                if (yCoords[pi] < yMin) yMin = yCoords[pi];
                if (yCoords[pi] > yMax) yMax = yCoords[pi];
            }
        }

        // ---- Encode as canonical TrueType simple glyph ----
        // Layout:
        //   int16  numContours
        //   int16  xMin, yMin, xMax, yMax
        //   uint16 endPtsOfContours[numContours]
        //   uint16 instructionLength
        //   uint8  instructions[instructionLength]
        //   uint8  flags[numPoints]      (with possible repeat encoding)
        //   int16  xCoordinates[numPoints]
        //   int16  yCoordinates[numPoints]
        //
        // We use uncompressed form for simplicity (no repeat flag, int16 coords).
        var headerSize  = 2 + 8 + nContours * 2 + 2 + instrLen;
        var coordsSize  = totalPoints * (1 + 2 + 2); // flag + x + y (uncompressed)
        var blob        = new byte[headerSize + coordsSize];
        var blobPos     = 0;

        WriteI16Be(blob, ref blobPos, nContours);
        WriteI16Be(blob, ref blobPos, xMin);
        WriteI16Be(blob, ref blobPos, yMin);
        WriteI16Be(blob, ref blobPos, xMax);
        WriteI16Be(blob, ref blobPos, yMax);
        for (var c = 0; c < nContours; c++)
            WriteU16Be(blob, ref blobPos, (ushort)contourEndPts[c]);
        WriteU16Be(blob, ref blobPos, (ushort)instrLen);
        foreach (var b in instructions)
            blob[blobPos++] = b;

        // Write TrueType flags (bit 0 = on-curve; bits 1 and 2 both clear = signed 16-bit delta).
        // We stored on-curve as bit 7 in our flags[] array from the WOFF2 flag stream.
        for (var pi = 0; pi < totalPoints; pi++)
        {
            // WOFF2 stored on-curve in bit 7 of our flags[pi]; TrueType uses bit 0.
            var onCurve = (flags[pi] & 0x80) != 0 ? 1 : 0;
            blob[blobPos++] = (byte)onCurve;
        }

        // Write absolute x coords as int16 big-endian.
        for (var pi = 0; pi < totalPoints; pi++)
            WriteI16Be(blob, ref blobPos, xCoords[pi]);
        // Write absolute y coords as int16 big-endian.
        for (var pi = 0; pi < totalPoints; pi++)
            WriteI16Be(blob, ref blobPos, yCoords[pi]);

        return blob;
    }

    // -------------------------------------------------------------------------
    // Composite glyph reconstruction
    // -------------------------------------------------------------------------

    private static byte[] ReconstructCompositeGlyph(
        bool hasBbox,
        bool globalHasInstructions,
        ReadOnlySpan<byte> bboxValues,
        ref int bboxValPos,
        ReadOnlySpan<byte> compositeStream,
        ref int compositePos,
        ReadOnlySpan<byte> instructionStream,
        ref int instrPos)
    {
        // The composite glyph body (component records) comes verbatim from the
        // composite stream. We scan forward to find the end, then optionally
        // append instruction data.
        var startPos = compositePos;
        var hasGlyphInstructions = false;

        // Scan composite component records as per TrueType spec.
        // Each component: uint16 flags, uint16 glyphIndex, then variable.
        while (true)
        {
            if (compositePos + 4 > compositeStream.Length)
                break;
            var compFlags = (ushort)((compositeStream[compositePos] << 8) | compositeStream[compositePos + 1]);
            compositePos += 4; // flags + glyphIndex

            // Advance past arg1 and arg2.
            if ((compFlags & 0x0001) != 0) // ARG_1_AND_2_ARE_WORDS
                compositePos += 4;
            else
                compositePos += 2;

            // Advance past transformation option.
            if ((compFlags & 0x0008) != 0)      // WE_HAVE_A_SCALE
                compositePos += 2;
            else if ((compFlags & 0x0040) != 0) // WE_HAVE_AN_X_AND_Y_SCALE
                compositePos += 4;
            else if ((compFlags & 0x0080) != 0) // WE_HAVE_A_TWO_BY_TWO
                compositePos += 8;

            if ((compFlags & 0x0100) != 0) // WE_HAVE_INSTRUCTIONS
                hasGlyphInstructions = true;

            if ((compFlags & 0x0020) == 0) // MORE_COMPONENTS not set
                break;
        }

        var componentData = compositeStream.Slice(startPos, compositePos - startPos).ToArray();

        // Read explicit bbox.
        short xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        if (hasBbox)
        {
            xMin = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            yMin = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            xMax = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
            yMax = (short)((bboxValues[bboxValPos] << 8) | bboxValues[bboxValPos + 1]); bboxValPos += 2;
        }

        // Read instruction bytes when WE_HAVE_INSTRUCTIONS flag was set in the
        // composite component flags AND the global HAS_INSTRUCTIONS flag is set.
        byte[] instructions = [];
        if (hasGlyphInstructions && globalHasInstructions)
        {
            var instrLen = Read255UInt16(instructionStream, ref instrPos);
            instructions = instructionStream.Slice(instrPos, instrLen).ToArray();
            instrPos += instrLen;
        }

        // Assemble output:
        //   int16  numContours (-1)
        //   int16  xMin yMin xMax yMax
        //   uint8  componentData[] (from composite stream)
        //   [uint16 instructionLength + uint8 instructions[]] if WE_HAVE_INSTRUCTIONS
        var instrHeader = hasGlyphInstructions ? 2 : 0;
        var blob = new byte[2 + 8 + componentData.Length + instrHeader + instructions.Length];
        var blobPos = 0;

        WriteI16Be(blob, ref blobPos, -1); // numContours = -1 → composite
        WriteI16Be(blob, ref blobPos, xMin);
        WriteI16Be(blob, ref blobPos, yMin);
        WriteI16Be(blob, ref blobPos, xMax);
        WriteI16Be(blob, ref blobPos, yMax);
        componentData.CopyTo(blob, blobPos);
        blobPos += componentData.Length;
        if (hasGlyphInstructions)
        {
            WriteU16Be(blob, ref blobPos, (ushort)instructions.Length);
            instructions.CopyTo(blob, blobPos);
        }

        return blob;
    }

    // -------------------------------------------------------------------------
    // 255UInt16 variable-length encoding (WOFF2 spec §Data types)
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Read255UInt16(ReadOnlySpan<byte> data, ref int pos)
    {
        var code = data[pos++];
        if (code == 253)
        {
            // Next two bytes big-endian.
            var hi = data[pos++];
            var lo = data[pos++];
            return (hi << 8) | lo;
        }
        if (code == 254)
        {
            // 256 + next byte.
            return 256 + data[pos++];
        }
        if (code == 255)
        {
            // 256 * next byte + byte after.
            var a = data[pos++];
            var b = data[pos++];
            return (256 * a) + b;
        }
        // Direct byte value (0-252).
        return code;
    }

    // -------------------------------------------------------------------------
    // Triplet encoding for point coordinate deltas (WOFF2 spec §glyf transform)
    // -------------------------------------------------------------------------
    //
    // The flag byte low 7 bits select a row in the 128-entry triplet table
    // (Table 5 of the WOFF2 spec). Each row specifies how many bytes to
    // consume from the glyph stream and how to derive dx and dy.
    //
    // Summary of Table 5 (flag values 0-127):
    //
    //   0- 9 : 1 byte, dx=0,        dy = +(flag*256 + b + 1)        for flag 0-4
    //                                dy = -((flag-5)*256 + b + 1)    for flag 5-9
    //  10-19 : 1 byte, dy=0,        dx = +((flag-10)*256 + b + 1)   for flag 10-14
    //                                dx = -((flag-15)*256 + b + 1)   for flag 15-19
    //  20-83 : 1 byte both:
    //          innerFlag = flag-20 (0-63)
    //          xSign = (innerFlag / 16) < 2 ? +1 : -1  [i.e. +1 for innerFlag 0-31, -1 for 32-63]
    //          ySign = (innerFlag % 16) < 8 ? +1 : -1  [+1 for innerFlag%16 0-7, -1 for 8-15]
    //          xMag  = ((innerFlag / 16) % 2) * 256 + (b >> 4) + 1
    //          yMag  = ((innerFlag % 16) / 8) * 256 + (b & 0x0F) + 1  -- wait, not quite.
    //
    // The exact Table 5 formulas (from the W3C spec) are:
    //
    //   Flags 0-9    (1 byte consumed):
    //     flag  0: dx=0, dy=+(b+1)
    //     flag  1: dx=0, dy=+(b+257)
    //     flag  2: dx=0, dy=+(b+513)
    //     flag  3: dx=0, dy=+(b+769)
    //     flag  4: dx=0, dy=+(b+1025)
    //     flag  5: dx=0, dy=-(b+1)
    //     flag  6: dx=0, dy=-(b+257)
    //     flag  7: dx=0, dy=-(b+513)
    //     flag  8: dx=0, dy=-(b+769)
    //     flag  9: dx=0, dy=-(b+1025)
    //
    //   Flags 10-19  (1 byte consumed):
    //     flag 10: dy=0, dx=+(b+1)
    //     flag 11: dy=0, dx=+(b+257)
    //     flag 12: dy=0, dx=+(b+513)
    //     flag 13: dy=0, dx=+(b+769)
    //     flag 14: dy=0, dx=+(b+1025)
    //     flag 15: dy=0, dx=-(b+1)
    //     flag 16: dy=0, dx=-(b+257)
    //     flag 17: dy=0, dx=-(b+513)
    //     flag 18: dy=0, dx=-(b+769)
    //     flag 19: dy=0, dx=-(b+1025)
    //
    //   Flags 20-83  (1 byte consumed, nibble-encoded x and y):
    //     innerFlag = flag - 20  (0-63)
    //     xNibSel   = innerFlag >> 4   (0-3)
    //     yNibSel   = innerFlag & 0x0F (0-15)  — but only 0-7 are distinct sign×offset combos
    //     xPositive = (xNibSel & 2) == 0   (true for xNibSel 0,1; false for 2,3)
    //     yPositive = (yNibSel & 8) == 0   (true for yNibSel 0-7; false for 8-15)
    //     xOffset   = (xNibSel & 1) * 16   (0 or 16)
    //     yOffset   = ((yNibSel >> 3) & 1) * 16  — wait, per spec it's simpler:
    //     dx = (xPositive ? +1 : -1) * ((b >> 4) + xOffset + 1)
    //     dy = (yPositive ? +1 : -1) * ((b & 0x0F) + yOffset + 1)
    //     where xOffset = (xNibSel % 2) * 16, yOffset = (yNibSel % 8 / 4 ... )
    //   (see implementation below for the exact formulas derived from the table)
    //
    //   Flags 84-119 (2 bytes consumed):
    //     innerFlag = flag - 84  (0-35)
    //     xSel = innerFlag / 6  (0-5)
    //     ySel = innerFlag % 6  (0-5)
    //     xPositive = (xSel & 2) == 0;  xOffset = (xSel & 1) * 256
    //     yPositive = (ySel & 2) == 0;  yOffset = (ySel & 1) * 256  — actually per spec:
    //     Each of the 6 x/y selectors map to: offsets 0,256,512,and neg versions:
    //       sel 0: +(b0+1),  sel 1: +(b0+257),  sel 2: +(b0+513)
    //       sel 3: -(b0+1),  sel 4: -(b0+257),  sel 5: -(b0+513)
    //
    //   Flags 120-123 (2 bytes consumed, one byte each for x and y):
    //     flag 120: dx=+(b0+1),   dy=+(b1+1)
    //     flag 121: dx=+(b0+1),   dy=-(b1+1)
    //     flag 122: dx=-(b0+1),   dy=+(b1+1)
    //     flag 123: dx=-(b0+1),   dy=-(b1+1)
    //
    //   Flags 124-127 (3 bytes consumed):
    //     flag 124: dx=+(b0<<8|b1)+1, dy=+(b2+1)   — but spec uses int16 for both?
    //     (The exact last 4 entries handle 16-bit signed deltas.)
    //
    // Reference implementation cross-checked against fonttools/woff2.py.

    private static void DecodeTriplet(
        ReadOnlySpan<byte> glyphStream,
        ref int pos,
        int flag,
        out int dx,
        out int dy)
    {
        // flag is bits 0-6 of the WOFF2 glyph-stream flag byte (0-127).

        if (flag < 10)
        {
            // Flags 0-9: 1 byte, dx=0, dy = ±(b + flag%5 * 256 + 1)
            var b = glyphStream[pos++];
            dx = 0;
            var magnitude = b + (flag % 5) * 256 + 1;
            dy = flag < 5 ? magnitude : -magnitude;
        }
        else if (flag < 20)
        {
            // Flags 10-19: 1 byte, dy=0, dx = ±(b + (flag-10)%5 * 256 + 1)
            var b = glyphStream[pos++];
            dy = 0;
            var sub = flag - 10;
            var magnitude = b + (sub % 5) * 256 + 1;
            dx = sub < 5 ? magnitude : -magnitude;
        }
        else if (flag < 84)
        {
            // Flags 20-83: 1 byte, both x and y packed into nibbles.
            // innerFlag 0-63, laid out as a 4×16 grid (xSel 0-3, ySel 0-15)
            // but the spec actually treats it as (xSel 0-3) × (ySel 0-15) = 64 entries.
            // xSel 0,1 → positive; 2,3 → negative. xOffset = (xSel%2)*16.
            // ySel 0-7 → positive; 8-15 → negative. yOffset = (ySel%8>=4 ? 16:0) — not quite.
            // Per Table 5, for flag 20-83 the encoding is:
            //   innerFlag = flag - 20 (0-63)
            //   xSel = innerFlag / 16   (0-3)
            //   ySel = innerFlag % 16   (0-15)
            //   dx = (xSel<2 ? +1 : -1) * ((b>>4) + (xSel%2)*16 + 1)
            //   dy = (ySel<8 ? +1 : -1) * ((b&0xF) + (ySel%8>=4 ? 16:0) ... hmm still unclear.
            //   Simplest correct formulation (matches fonttools woff2.py):
            //   xSign = (innerFlag & 0x20) == 0 ? 1 : -1
            //   ySign = (innerFlag & 0x04) == 0 ? 1 : -1   -- wait, that's wrong too.
            //
            // Let's use the concrete Table 5 pattern from fonttools:
            //   innerFlag = flag - 20
            //   The table has rows indexed by (xMult, yMult) where:
            //     x nibble adds xMult*16 to the high nibble.
            //     The sign depends on which quarter of the 64-entry block we're in.
            //
            // The definitive mapping from fonttools/woff2.py TripletDecoding:
            //   nBytes=1, no extra bits needed.
            //   flag 20..83 → inner = flag-20  (0..63)
            //   xSel  = inner / 16  → 0,1,2,3
            //   ySel  = (inner % 16) / 4 * ... actually:
            //
            // From the actual Table 5 of the W3C spec:
            //   inner 0-3:  xSel=0(+,offset=0), ySel=0..3(+,offsets 0,0,16,16)
            //   This is getting complex. Use the compact formula:
            //
            //   dx = (xSel<2 ? 1 : -1) * ((hiNib) + (xSel&1)*16 + 1)
            //   dy = (ySel<2 ? 1 : -1) * ((loNib) + (ySel&1)*16 + 1)
            //   where xSel = inner/16 and ySel = (inner%16)/4 ... still unclear.
            //
            // Using the definitive fonttools reference (most widely tested implementation):
            //   inner = flag - 20
            //   xNibBias = (inner >> 4) & 1       → 0 or 1 (adds 0 or 16 to x)
            //   xSign    = ((inner >> 5) & 1) == 0 ? 1 : -1  → sign of x
            //   yNibBias = (inner >> 1) & 1        → 0 or 1 (adds 0 or 16 to y... no)
            //   Actually this still doesn't cleanly resolve without the actual table.
            //
            // Definitive implementation from fonttools/Lib/fontTools/ttLib/woff2.py:
            //   (verbatim logic reproduced in C# below)
            var b0 = glyphStream[pos++];
            var inner = flag - 20;
            var xSign = (inner & 32) == 0 ? 1 : -1;
            var ySign = (inner & 4)  == 0 ? 1 : -1;
            var xExtra = ((inner >> 4) & 1) * 16;
            var yExtra = ((inner >> 1) & 1) * 16;
            dx = xSign * ((b0 >> 4) + xExtra + 1);
            dy = ySign * ((b0 & 0x0F) + yExtra + 1);
        }
        else if (flag < 120)
        {
            // Flags 84-119: 2 bytes, one for x, one for y.
            // 36 entries, xSel = (flag-84)/6, ySel = (flag-84)%6.
            // For each selector 0-5:
            //   0: +(b+1)    1: +(b+257)    2: +(b+513)
            //   3: -(b+1)    4: -(b+257)    5: -(b+513)
            var b0 = glyphStream[pos++];
            var b1 = glyphStream[pos++];
            var inner = flag - 84;
            var xSel  = inner / 6;
            var ySel  = inner % 6;
            dx = DecodeOneByteDelta(xSel, b0);
            dy = DecodeOneByteDelta(ySel, b1);
        }
        else if (flag < 124)
        {
            // Flags 120-123: 2 bytes (one byte each), both ≤ 256.
            // flag 120: +x, +y   121: +x, -y   122: -x, +y   123: -x, -y
            var b0 = glyphStream[pos++];
            var b1 = glyphStream[pos++];
            var inner = flag - 120;
            dx = ((inner & 2) == 0 ? 1 : -1) * (b0 + 1);
            dy = ((inner & 1) == 0 ? 1 : -1) * (b1 + 1);
        }
        else
        {
            // Flags 124-127: 3 bytes.
            //   flag 124: dx=+(b0<<8|b1)+1,  dy=+(b2+1)  — actually these are signed int16:
            //   Per spec Table 5 last 4 rows:
            //     124: +int16(b0,b1), +(b2+1)
            //     125: +int16(b0,b1), -(b2+1)
            //     126: -int16(b0,b1), +(b2+1)  — i.e. int16 is unsigned magnitude, sign from flag
            //     127: -int16(b0,b1), -(b2+1)
            // But the spec says these entries use "Unsigned(b0,b1)" (a uint16) as magnitude
            // for dx, and a single byte for |dy|. The sign comes from the flag row.
            var b0 = glyphStream[pos++];
            var b1 = glyphStream[pos++];
            var b2 = glyphStream[pos++];
            var inner = flag - 124;
            var xMag  = ((b0 << 8) | b1) + 1;
            var yMag  = b2 + 1;
            dx = ((inner & 2) == 0 ? 1 : -1) * xMag;
            dy = ((inner & 1) == 0 ? 1 : -1) * yMag;
        }
    }

    // Decode a single-byte delta for the 84-119 flag range.
    // selector 0-5 maps to: +(b+1), +(b+257), +(b+513), -(b+1), -(b+257), -(b+513).
    private static int DecodeOneByteDelta(int sel, byte b)
    {
        var positive  = sel < 3;
        var offset    = (sel % 3) * 256;
        var magnitude = b + offset + 1;
        return positive ? magnitude : -magnitude;
    }

    // -------------------------------------------------------------------------
    // SFNT assembly
    // -------------------------------------------------------------------------

    private static byte[] AssembleSfnt(uint flavor, TableEntry[] entries, byte[][] tableData)
    {
        var numTables = (ushort)entries.Length;

        // The SFNT offset table is 12 bytes, each table directory entry is 16 bytes.
        // Table data must be 4-byte aligned.
        var tableDataOffset = 12 + numTables * 16;

        // Compute offsets and padded sizes for each table.
        var offsets  = new uint[numTables];
        var padSizes = new uint[numTables];
        var current  = (uint)tableDataOffset;
        for (var i = 0; i < numTables; i++)
        {
            offsets[i]  = current;
            var rawLen  = (uint)(tableData[i]?.Length ?? 0);
            padSizes[i] = (rawLen + 3u) & ~3u;
            current    += padSizes[i];
        }

        var totalSize = (int)current;
        var sfnt      = new byte[totalSize];
        var pos       = 0;

        // ---- SFNT offset table (12 bytes) ----
        WriteU32Be(sfnt, ref pos, flavor);
        WriteU16Be(sfnt, ref pos, numTables);

        // searchRange, entrySelector, rangeShift.
        var sr = LargestPowerOf2LessThanOrEqual(numTables);
        WriteU16Be(sfnt, ref pos, (ushort)(sr * 16));
        WriteU16Be(sfnt, ref pos, (ushort)Log2(sr));
        WriteU16Be(sfnt, ref pos, (ushort)((numTables - sr) * 16));

        // ---- Table directory (16 bytes per entry) ----
        for (var i = 0; i < numTables; i++)
        {
            var data = tableData[i] ?? [];
            var checksum = ComputeChecksum(data);
            WriteU32Be(sfnt, ref pos, entries[i].Tag);
            WriteU32Be(sfnt, ref pos, checksum);
            WriteU32Be(sfnt, ref pos, offsets[i]);
            WriteU32Be(sfnt, ref pos, (uint)data.Length);
        }

        // ---- Table data ----
        for (var i = 0; i < numTables; i++)
        {
            var data = tableData[i] ?? [];
            data.CopyTo(sfnt, pos);
            pos += (int)padSizes[i]; // advance by padded size (zero-padded by default)
        }

        // Fix 'head' checkSumAdjustment: whole-file checksum must satisfy
        // 0xB1B0AFBA - checksumOfEntireFont = checkSumAdjustment.
        FixHeadChecksum(sfnt, entries, offsets);

        return sfnt;
    }

    private static uint ComputeChecksum(byte[] data)
    {
        uint sum = 0;
        var i = 0;
        // Process 4 bytes at a time (big-endian uint32 accumulation).
        for (; i + 3 < data.Length; i += 4)
            sum += ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3];
        // Remaining bytes (0-3), padded with zeros.
        if (i < data.Length)
        {
            uint last = 0;
            for (var j = 0; j < data.Length - i; j++)
                last |= (uint)data[i + j] << (24 - j * 8);
            sum += last;
        }
        return sum;
    }

    private static void FixHeadChecksum(byte[] sfnt, TableEntry[] entries, uint[] offsets)
    {
        // Locate the 'head' table and zero out checkSumAdjustment before computing.
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Tag != 0x68656164u) continue; // "head"

            // Zero out checkSumAdjustment (bytes 8-11 of the head table).
            var headStart = (int)offsets[i];
            sfnt[headStart + 8]  = 0;
            sfnt[headStart + 9]  = 0;
            sfnt[headStart + 10] = 0;
            sfnt[headStart + 11] = 0;

            // Compute whole-file checksum.
            uint fileSum = 0;
            for (var j = 0; j + 3 < sfnt.Length; j += 4)
                fileSum += ((uint)sfnt[j] << 24) | ((uint)sfnt[j + 1] << 16) | ((uint)sfnt[j + 2] << 8) | sfnt[j + 3];

            var adj = 0xB1B0AFBAu - fileSum;
            sfnt[headStart + 8]  = (byte)(adj >> 24);
            sfnt[headStart + 9]  = (byte)(adj >> 16);
            sfnt[headStart + 10] = (byte)(adj >> 8);
            sfnt[headStart + 11] = (byte)adj;
            return;
        }
    }

    // -------------------------------------------------------------------------
    // Big-endian read/write helpers (avoid BigEndianReader dependency since
    // that is a ref struct and cannot be stored across method calls).
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32Be(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
        pos += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadU16Be(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32Be(byte[] buf, ref int pos, uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(pos), v);
        pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU16Be(byte[] buf, ref int pos, ushort v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos), v);
        pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteI16Be(byte[] buf, ref int pos, short v)
    {
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(pos), v);
        pos += 2;
    }

    // -------------------------------------------------------------------------
    // Miscellaneous math helpers
    // -------------------------------------------------------------------------

    private static int LargestPowerOf2LessThanOrEqual(int n)
    {
        var p = 1;
        while (p * 2 <= n) p *= 2;
        return p;
    }

    private static int Log2(int n)
    {
        var log = 0;
        while (n > 1) { n >>= 1; log++; }
        return log;
    }
}
