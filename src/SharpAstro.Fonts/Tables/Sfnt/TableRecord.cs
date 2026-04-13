using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Sfnt;

/// <summary>
/// One entry in the SFNT table directory.
/// </summary>
public readonly record struct TableRecord(Tag Tag, uint Checksum, uint Offset, uint Length)
{
    /// <summary>Slice of the original font data covering this table.</summary>
    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> fontData)
        => fontData.Slice((int)Offset, (int)Length);
}
