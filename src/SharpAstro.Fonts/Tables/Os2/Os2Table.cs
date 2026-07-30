using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Os2;

/// <summary>
/// Parsed 'OS/2' table — weight/width/style classification plus the coverage bitmaps.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/os2
///
/// <para>Fields added by later table versions are exposed as nullable and are null on fonts
/// that predate them; <see cref="CodePageRange1"/> in particular arrived in version 1.</para>
/// </summary>
public sealed class Os2Table
{
    public ushort Version { get; }
    /// <summary>usWeightClass, 1–1000 (400 = Regular, 700 = Bold).</summary>
    public ushort WeightClass { get; }
    /// <summary>usWidthClass, 1–9 (5 = Medium/normal).</summary>
    public ushort WidthClass { get; }
    /// <summary>sFamilyClass. The high byte is the class, the low byte the subclass;
    /// class 12 is "Symbolic".</summary>
    public short FamilyClass { get; }
    /// <summary>The 10 PANOSE digits. Digit 0 (bFamilyType) is 5 for "Latin Pictorial",
    /// i.e. a legacy symbol/dingbat font.</summary>
    public byte[] Panose { get; }
    public uint UnicodeRange1 { get; }
    public uint UnicodeRange2 { get; }
    public uint UnicodeRange3 { get; }
    public uint UnicodeRange4 { get; }
    /// <summary>fsSelection style bits.</summary>
    public ushort FsSelection { get; }
    /// <summary>ulCodePageRange1 (version ≥ 1). Bit 31 is the legacy "Symbol character set".</summary>
    public uint? CodePageRange1 { get; }
    public uint? CodePageRange2 { get; }

    /// <summary>fsSelection bit 0.</summary>
    public bool IsItalic => (FsSelection & 0x0001) != 0;
    /// <summary>fsSelection bit 5.</summary>
    public bool IsBold => (FsSelection & 0x0020) != 0;
    /// <summary>fsSelection bit 6 — set on the family's regular face.</summary>
    public bool IsRegular => (FsSelection & 0x0040) != 0;
    /// <summary>fsSelection bit 9 — oblique, distinct from italic.</summary>
    public bool IsOblique => (FsSelection & 0x0200) != 0;

    /// <summary>PANOSE bFamilyType 5 = Latin Pictorial (Wingdings, ZapfDingbats, Symbol).</summary>
    public bool IsPictorialPanose => Panose.Length > 0 && Panose[0] == 5;

    /// <summary>sFamilyClass 12 = Symbolic.</summary>
    public bool IsSymbolicFamilyClass => (FamilyClass >> 8) == 12;

    /// <summary>ulCodePageRange1 bit 31 — the font declares the legacy Symbol character set.</summary>
    public bool DeclaresSymbolCodePage => (CodePageRange1 & (1u << 31)) != 0;

    private Os2Table(ushort version, ushort weightClass, ushort widthClass, short familyClass,
        byte[] panose, uint ur1, uint ur2, uint ur3, uint ur4, ushort fsSelection,
        uint? cpr1, uint? cpr2)
    {
        Version = version;
        WeightClass = weightClass;
        WidthClass = widthClass;
        FamilyClass = familyClass;
        Panose = panose;
        UnicodeRange1 = ur1;
        UnicodeRange2 = ur2;
        UnicodeRange3 = ur3;
        UnicodeRange4 = ur4;
        FsSelection = fsSelection;
        CodePageRange1 = cpr1;
        CodePageRange2 = cpr2;
    }

    // Byte offsets into the table of the fields we read past the fixed prologue.
    private const int FamilyClassOffset = 30;   // after version/xAvg/weight/width/fsType + 10×int16 metrics
    private const int FsSelectionOffset = 62;   // after panose(10) + unicodeRange(16) + achVendID(4)
    private const int CodePageRangeOffset = 78; // after usFirst/LastCharIndex, sTypo*, usWin*

    public static Os2Table Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt16();
        r.Skip(2);                          // xAvgCharWidth
        var weightClass = r.ReadUInt16();
        var widthClass = r.ReadUInt16();
        r.Skip(2);                          // fsType

        r.Position = FamilyClassOffset;
        var familyClass = r.ReadInt16();

        var panose = new byte[10];
        for (var i = 0; i < panose.Length; i++) panose[i] = r.ReadByte();

        var ur1 = r.ReadUInt32();
        var ur2 = r.ReadUInt32();
        var ur3 = r.ReadUInt32();
        var ur4 = r.ReadUInt32();

        r.Position = FsSelectionOffset;
        var fsSelection = r.ReadUInt16();

        // Version 0 tables stop before the code-page ranges, and a handful of old fonts truncate
        // even earlier than the spec's version-0 length — bounds-check rather than trust version.
        uint? cpr1 = null, cpr2 = null;
        if (version >= 1 && data.Length >= CodePageRangeOffset + 8)
        {
            r.Position = CodePageRangeOffset;
            cpr1 = r.ReadUInt32();
            cpr2 = r.ReadUInt32();
        }

        return new Os2Table(version, weightClass, widthClass, familyClass, panose,
            ur1, ur2, ur3, ur4, fsSelection, cpr1, cpr2);
    }
}
