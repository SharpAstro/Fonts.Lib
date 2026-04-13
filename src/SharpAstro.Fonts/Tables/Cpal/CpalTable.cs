using SharpAstro.Fonts.Color;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cpal;

/// <summary>
/// Parsed 'CPAL' (Color Palette) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/cpal
///
/// <para>One or more palettes, each containing the same number of color
/// entries. Color records are stored on disk as B,G,R,A — converted to
/// R,G,B,A in memory.</para>
/// </summary>
public sealed class CpalTable
{
    public ushort NumPalettes { get; }
    public ushort NumPaletteEntries { get; }
    /// <summary>palettes[i][j] — j-th color of i-th palette.</summary>
    private readonly Rgba32[][] _palettes;

    private CpalTable(ushort numPalettes, ushort numEntries, Rgba32[][] palettes)
    {
        NumPalettes = numPalettes;
        NumPaletteEntries = numEntries;
        _palettes = palettes;
    }

    /// <summary>
    /// Get one color from the palette. Out-of-range indices return
    /// <see cref="Rgba32.Black"/> (matches FreeType's fallback).
    /// </summary>
    public Rgba32 GetColor(int paletteIndex, int colorIndex)
    {
        if ((uint)paletteIndex >= (uint)_palettes.Length) return Rgba32.Black;
        var pal = _palettes[paletteIndex];
        if ((uint)colorIndex >= (uint)pal.Length) return Rgba32.Black;
        return pal[colorIndex];
    }

    public ReadOnlySpan<Rgba32> GetPalette(int paletteIndex)
        => paletteIndex >= 0 && paletteIndex < _palettes.Length
            ? _palettes[paletteIndex]
            : [];

    public static CpalTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt16();
        var numEntries = r.ReadUInt16();
        var numPalettes = r.ReadUInt16();
        var numColorRecords = r.ReadUInt16();
        var firstColorOffset = r.ReadUInt32();

        var paletteIndices = new ushort[numPalettes];
        for (var i = 0; i < numPalettes; i++)
            paletteIndices[i] = r.ReadUInt16();

        // Color records: BGRA bytes.
        var records = new Rgba32[numColorRecords];
        var cr = new BigEndianReader(data, (int)firstColorOffset);
        for (var i = 0; i < numColorRecords; i++)
        {
            var b = cr.ReadByte();
            var g = cr.ReadByte();
            var rd = cr.ReadByte();
            var a = cr.ReadByte();
            records[i] = new Rgba32(rd, g, b, a);
        }

        var palettes = new Rgba32[numPalettes][];
        for (var i = 0; i < numPalettes; i++)
        {
            var start = paletteIndices[i];
            var pal = new Rgba32[numEntries];
            for (var j = 0; j < numEntries && start + j < records.Length; j++)
                pal[j] = records[start + j];
            palettes[i] = pal;
        }

        _ = version;
        return new CpalTable(numPalettes, numEntries, palettes);
    }
}
