using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Sfnt;

/// <summary>
/// Parsed TrueType Collection (TTC) header — the wrapper format used to bundle
/// multiple SFNT fonts into a single .ttc file (e.g. cambria.ttc on Windows
/// holds Cambria, Cambria Bold, Cambria Italic, Cambria Bold Italic, and
/// Cambria Math). Each entry in <see cref="OffsetTable"/> points to a regular
/// SFNT offset table inside the same byte buffer; loading a face is just a
/// matter of constructing an <see cref="OpenTypeFont"/> at that offset.
///
/// Spec: https://learn.microsoft.com/typography/opentype/spec/otff#font-collections
///
/// Layout:
/// <list type="bullet">
///   <item>uint32 ttcTag — must be 'ttcf' (0x74746366)</item>
///   <item>uint16 majorVersion — 1 or 2</item>
///   <item>uint16 minorVersion — 0</item>
///   <item>uint32 numFonts — number of faces in the collection</item>
///   <item>uint32[] offsetTable — file offset of each face's offset table</item>
///   <item>(v2 only) uint32 dsigTag, uint32 dsigLength, uint32 dsigOffset —
///         optional DSIG (digital signature) trailer; ignored here.</item>
/// </list>
/// </summary>
public sealed class TtcHeader
{
    /// <summary>'ttcf' big-endian (the magic at offset 0 of every TTC).</summary>
    public const uint TtcfMagic = 0x74746366u;

    /// <summary>Header major version: 1 (no DSIG fields) or 2 (DSIG fields present).</summary>
    public ushort MajorVersion { get; }

    /// <summary>Header minor version. Always 0 in current spec versions.</summary>
    public ushort MinorVersion { get; }

    /// <summary>File offsets of each face's SFNT offset table. Length == numFonts.</summary>
    public uint[] OffsetTable { get; }

    /// <summary>Number of faces in the collection.</summary>
    public int NumFonts => OffsetTable.Length;

    private TtcHeader(ushort major, ushort minor, uint[] offsets)
    {
        MajorVersion = major;
        MinorVersion = minor;
        OffsetTable = offsets;
    }

    /// <summary>
    /// Quick magic check at the start of <paramref name="data"/>. Does not
    /// allocate or validate further — callers that need details should use
    /// <see cref="Parse"/>. Use this to disambiguate TTC vs. plain SFNT in
    /// <c>OpenTypeFont.Load</c> before deciding which parser to invoke.
    /// </summary>
    public static bool IsTtc(ReadOnlySpan<byte> data)
        => data.Length >= 4
           && data[0] == 0x74 && data[1] == 0x74
           && data[2] == 0x63 && data[3] == 0x66;

    /// <summary>
    /// Parse the TTC header. Throws <see cref="InvalidDataException"/> if the
    /// magic is wrong, the version is unsupported, or numFonts overflows the
    /// available bytes.
    /// </summary>
    public static TtcHeader Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var tag = r.ReadUInt32();
        if (tag != TtcfMagic)
            throw new InvalidDataException(
                $"Not a TTC font collection (expected 'ttcf' at offset 0, got 0x{tag:X8}).");

        var major = r.ReadUInt16();
        var minor = r.ReadUInt16();
        if (major != 1 && major != 2)
            throw new InvalidDataException(
                $"Unsupported TTC version {major}.{minor} (expected 1.0 or 2.0).");

        var numFonts = r.ReadUInt32();
        if (numFonts == 0)
            throw new InvalidDataException("TTC declares zero faces.");
        // Defensive bound: prevent ridiculous numFonts from blowing up the array
        // alloc or causing an OverflowException on the Slice math below. A real
        // collection has at most a few dozen faces.
        if (numFonts > 0x10000)
            throw new InvalidDataException(
                $"TTC numFonts={numFonts} is implausibly large.");

        var offsets = new uint[numFonts];
        for (var i = 0; i < numFonts; i++)
        {
            var off = r.ReadUInt32();
            if (off >= data.Length)
                throw new InvalidDataException(
                    $"TTC face {i} offset 0x{off:X} is past the end of the buffer (length {data.Length}).");
            offsets[i] = off;
        }

        // v2 has a DSIG trailer (dsigTag, dsigLength, dsigOffset). We don't use
        // it; just skip past so any future readers picking up where we left off
        // see the right position. No effect since we only return parsed offsets.

        return new TtcHeader(major, minor, offsets);
    }
}
