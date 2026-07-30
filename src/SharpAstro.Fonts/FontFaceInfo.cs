using Microsoft.Win32.SafeHandles;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Tables.Name;
using SharpAstro.Fonts.Tables.Os2;
using SharpAstro.Fonts.Tables.Sfnt;

namespace SharpAstro.Fonts;

/// <summary>
/// Identity and style of one face, as the face itself declares it. This is what a font
/// <em>index</em> needs — enough to answer "which file (and which face inside it) is
/// 'Segoe UI Symbol' Bold?" — and deliberately nothing more.
/// </summary>
/// <param name="Path">Absolute path of the file the face lives in.</param>
/// <param name="FaceIndex">Index within a TTC/OTC; 0 for a plain single-face file.</param>
/// <param name="Family">Typographic family if the face declares one, else the legacy family.</param>
/// <param name="Subfamily">Style within <paramref name="Family"/>.</param>
/// <param name="LegacyFamily">Name ID 1 — the four-face style-linked family.</param>
/// <param name="LegacySubfamily">Name ID 2.</param>
/// <param name="FullName">Name ID 4.</param>
/// <param name="PostScriptName">Name ID 6.</param>
/// <param name="WeightClass">OS/2 usWeightClass (400 = Regular, 700 = Bold); 0 if unknown.</param>
/// <param name="IsBold">OS/2 fsSelection bold bit, falling back to the subfamily name.</param>
/// <param name="IsItalic">OS/2 fsSelection italic/oblique bit, falling back to the subfamily name.</param>
public readonly record struct FontFaceInfo(
    string Path,
    int FaceIndex,
    string? Family,
    string? Subfamily,
    string? LegacyFamily,
    string? LegacySubfamily,
    string? FullName,
    string? PostScriptName,
    ushort WeightClass,
    bool IsBold,
    bool IsItalic);

/// <summary>
/// Reads face identity out of a font file <b>without loading the font</b>.
///
/// <para>Building an index of installed fonts means touching every file in the system font
/// directories — several hundred, including multi-megabyte CJK collections. Going through
/// <see cref="OpenTypeFont.LoadFromFile(string)"/> would read every one of those bytes to
/// recover a few hundred bytes of names. This reader instead seeks: SFNT/TTC header, then the
/// per-face table directory, then just the 'name' and 'OS/2' tables — a few KB per face
/// regardless of file size.</para>
/// </summary>
public static class FontFaceReader
{
    // Enough for the TTC header plus a generous offset table; larger collections read the rest
    // in a second pass. Sized to cover the SFNT offset table (12B) in the same first read.
    private const int HeaderProbeBytes = 12;
    private const int TableRecordBytes = 16;
    private const int OffsetTableBytes = 12;

    /// <summary>
    /// Every face in <paramref name="path"/> — one entry for a plain .ttf/.otf, N for a
    /// collection. Returns an empty array for a file that isn't a readable font: an index scan
    /// walks whatever the font directories happen to contain, so unreadable or malformed files
    /// are a normal occurrence, not an error.
    /// </summary>
    public static FontFaceInfo[] ReadFaces(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadFaces(handle, path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static FontFaceInfo[] ReadFaces(SafeFileHandle handle, string path)
    {
        Span<byte> header = stackalloc byte[HeaderProbeBytes];
        if (!TryReadExact(handle, header, 0)) return [];

        if (!TtcHeader.IsTtc(header))
        {
            var single = ReadFace(handle, path, faceOffset: 0, faceIndex: 0);
            return single is { } f ? [f] : [];
        }

        // TTC header: tag(4) + version(4) + numFonts(4), then numFonts × uint32 face offsets.
        var numFonts = (int)ReadUInt32(header[8..]);
        if (numFonts <= 0 || numFonts > 0xFFFF) return [];

        var offsetTable = new byte[numFonts * 4];
        if (!TryReadExact(handle, offsetTable, HeaderProbeBytes)) return [];

        var faces = new List<FontFaceInfo>(numFonts);
        for (var i = 0; i < numFonts; i++)
        {
            var faceOffset = (long)ReadUInt32(offsetTable.AsSpan(i * 4));
            if (ReadFace(handle, path, faceOffset, i) is { } face) faces.Add(face);
        }
        return [.. faces];
    }

    private static FontFaceInfo? ReadFace(SafeFileHandle handle, string path, long faceOffset, int faceIndex)
    {
        Span<byte> offsetTable = stackalloc byte[OffsetTableBytes];
        if (!TryReadExact(handle, offsetTable, faceOffset)) return null;

        var numTables = (int)ReadUInt16(offsetTable[4..]);
        if (numTables <= 0 || numTables > 512) return null;

        var directory = new byte[numTables * TableRecordBytes];
        if (!TryReadExact(handle, directory, faceOffset + OffsetTableBytes)) return null;

        // Table offsets in a TTC are absolute file offsets (not relative to the face), so the
        // records can be used as-is for both shapes.
        if (!TryFindTable(directory, numTables, Tags.Name, out var nameOffset, out var nameLength))
            return null;

        var nameBytes = new byte[nameLength];
        if (!TryReadExact(handle, nameBytes, nameOffset)) return null;

        NameTable name;
        try { name = NameTable.Parse(nameBytes); }
        catch (Exception) { return null; }

        Os2Table? os2 = null;
        if (TryFindTable(directory, numTables, Tags.OS2, out var os2Offset, out var os2Length)
            && os2Length >= 64)
        {
            var os2Bytes = new byte[os2Length];
            if (TryReadExact(handle, os2Bytes, os2Offset))
            {
                try { os2 = Os2Table.Parse(os2Bytes); }
                catch (Exception) { os2 = null; }
            }
        }

        var subfamily = name.Subfamily ?? name.LegacySubfamily;
        // OS/2 is authoritative when present; a handful of faces ship no OS/2 at all, and their
        // subfamily string ("Bold Italic") is then the only style signal available.
        var bold = os2?.IsBold ?? NameContains(subfamily, "bold");
        var italic = os2 is not null
            ? os2.IsItalic || os2.IsOblique
            : NameContains(subfamily, "italic") || NameContains(subfamily, "oblique");

        return new FontFaceInfo(
            path, faceIndex,
            name.Family, subfamily,
            name.LegacyFamily, name.LegacySubfamily,
            name.FullName, name.PostScriptName,
            os2?.WeightClass ?? 0, bold, italic);
    }

    private static bool NameContains(string? subfamily, string token)
        => subfamily is not null && subfamily.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool TryFindTable(ReadOnlySpan<byte> directory, int numTables, Tag tag,
        out long offset, out int length)
    {
        for (var i = 0; i < numTables; i++)
        {
            var rec = directory[(i * TableRecordBytes)..];
            if (ReadUInt32(rec) != tag.Value) continue;
            offset = ReadUInt32(rec[8..]);
            length = (int)ReadUInt32(rec[12..]);
            // A zero-length or absurd record is corruption; treat it as absent.
            if (length > 0 && length <= 1 << 22) return true;
            break;
        }
        offset = 0;
        length = 0;
        return false;
    }

    // RandomAccess.Read is permitted to return a short read; loop until the buffer is full or
    // the file ends (a truncated table means the face is unusable, so a short read is a failure).
    private static bool TryReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer[read..], offset + read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b)
        => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static ushort ReadUInt16(ReadOnlySpan<byte> b) => (ushort)((b[0] << 8) | b[1]);
}
