using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// A GPOS ValueRecord: the placement/advance adjustments a positioning subtable
/// applies to a glyph. Fields present are selected by a ValueFormat bitmask, so a
/// record's on-disk size is variable (<see cref="Size"/>). We read the four
/// horizontal-layout fields and skip the four Device/VariationIndex offset fields —
/// device tables are hinting-grid deltas (irrelevant to SDF/scalable rendering) and
/// variation deltas are the deferred IVT path (plan non-goal).
///
/// <para>Values are in font design units and are <em>deltas</em>: XPlacement/YPlacement
/// shift the glyph's drawn position; XAdvance changes the pen advance. This maps
/// straight onto <see cref="ShapeBuffer"/>'s delta arrays — the engine never stores an
/// absolute advance (that stays with the renderer's glyph cache, per the A2 contract).</para>
/// </summary>
internal readonly record struct ValueRecord(short XPlacement, short YPlacement, short XAdvance, short YAdvance)
{
    /// <summary>Byte size of a record with this ValueFormat (2 bytes per set bit).</summary>
    public static int Size(ushort valueFormat)
    {
        var bits = 0;
        var v = valueFormat;
        while (v != 0) { bits += v & 1; v >>= 1; }
        return bits * 2;
    }

    /// <summary>
    /// Read a ValueRecord from <paramref name="r"/> per <paramref name="valueFormat"/>,
    /// advancing the reader past the full record (including skipped device-table offsets).
    /// </summary>
    public static ValueRecord Read(ref BigEndianReader r, ushort valueFormat)
    {
        // Bit order (low→high): XPlacement, YPlacement, XAdvance, YAdvance,
        // XPlaDevice, YPlaDevice, XAdvDevice, YAdvDevice.
        short xPla = 0, yPla = 0, xAdv = 0, yAdv = 0;
        if ((valueFormat & 0x01) != 0) xPla = r.ReadInt16();
        if ((valueFormat & 0x02) != 0) yPla = r.ReadInt16();
        if ((valueFormat & 0x04) != 0) xAdv = r.ReadInt16();
        if ((valueFormat & 0x08) != 0) yAdv = r.ReadInt16();
        if ((valueFormat & 0x10) != 0) r.Skip(2); // XPlaDevice
        if ((valueFormat & 0x20) != 0) r.Skip(2); // YPlaDevice
        if ((valueFormat & 0x40) != 0) r.Skip(2); // XAdvDevice
        if ((valueFormat & 0x80) != 0) r.Skip(2); // YAdvDevice
        return new ValueRecord(xPla, yPla, xAdv, yAdv);
    }
}
