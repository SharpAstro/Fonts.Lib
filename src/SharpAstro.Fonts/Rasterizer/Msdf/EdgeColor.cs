namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// The channel mask an edge contributes to (msdfgen's edge colours). Each glyph
/// edge is assigned a colour so that at a sharp corner the two incident edges
/// differ in at least one channel; <c>median(r,g,b)</c> then reconstructs the
/// true distance while the per-channel minima keep the corner sharp.
/// Bits: R=1, G=2, B=4.
/// </summary>
[Flags]
internal enum EdgeColor
{
    Black = 0,
    Red = 1,
    Green = 2,
    Yellow = 3,
    Blue = 4,
    Magenta = 5,
    Cyan = 6,
    White = 7,
}
