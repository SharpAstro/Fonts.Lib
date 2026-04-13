using System.Collections.Frozen;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Sfnt;

/// <summary>
/// Parsed SFNT offset table + table directory.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/otff
/// </summary>
public sealed class SfntDirectory
{
    /// <summary>SFNT version. Common values: 0x00010000 (TT), 'OTTO' (CFF), 'true', 'typ1'.</summary>
    public uint SfntVersion { get; }

    /// <summary>Whether this font uses TrueType outlines (glyf/loca).</summary>
    public bool IsTrueType => SfntVersion == 0x00010000 || SfntVersion == 0x74727565; // 'true'

    /// <summary>Whether this font uses CFF outlines.</summary>
    public bool IsCff => SfntVersion == 0x4F54544F; // 'OTTO'

    public FrozenDictionary<Tag, TableRecord> Tables { get; }

    private SfntDirectory(uint sfntVersion, FrozenDictionary<Tag, TableRecord> tables)
    {
        SfntVersion = sfntVersion;
        Tables = tables;
    }

    public bool TryGet(Tag tag, out TableRecord record) => Tables.TryGetValue(tag, out record);

    /// <summary>
    /// Parse the offset table + directory from the start of an SFNT or one
    /// face within a TTC. Pass the offset of the offset table within
    /// <paramref name="data"/> if loading a TTC face.
    /// </summary>
    public static SfntDirectory Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        var r = new BigEndianReader(data, offset);
        var sfntVersion = r.ReadUInt32();
        var numTables = r.ReadUInt16();
        // searchRange (uint16) + entrySelector (uint16) + rangeShift (uint16)
        r.Skip(6);

        var dict = new Dictionary<Tag, TableRecord>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var tag = r.ReadTag();
            var checksum = r.ReadUInt32();
            var off = r.ReadUInt32();
            var len = r.ReadUInt32();
            // Last record wins on duplicates (extremely rare; spec disallows but
            // some real-world fonts ship them).
            dict[tag] = new TableRecord(tag, checksum, off, len);
        }

        return new SfntDirectory(sfntVersion, dict.ToFrozenDictionary());
    }
}
