using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// One quadrant of the per-glyph "cut-in" kerning data attached to a math
/// base. A <see cref="MathKern"/> describes how the corner of a glyph
/// recedes from the bounding box at various heights, so a sub/super attached
/// to that corner can be tucked closer to the base than its rectangular
/// advance would allow (think: subscript on a "y" pulled left under the
/// descender, or superscript on a "V" pushed right into the slope).
///
/// <para>The data is a step function over correction heights. Given a query
/// height <i>h</i> (FUnits, measured from the base's baseline up for top
/// corners or down for bottom corners), <see cref="Lookup(short)"/> returns
/// the kern adjustment to apply to the script's horizontal position.</para>
///
/// <para>Spec: MathKern subtable in
/// <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/math#mathkern-table"/>.</para>
/// </summary>
public sealed class MathKern
{
    // Strictly increasing per spec. Length = N.
    private readonly short[] _correctionHeights;
    // Length = N + 1. _kernValues[i] applies for heights ≤ _correctionHeights[i];
    // _kernValues[N] applies for heights above the last correction height.
    private readonly short[] _kernValues;

    private MathKern(short[] correctionHeights, short[] kernValues)
    {
        _correctionHeights = correctionHeights;
        _kernValues = kernValues;
    }

    /// <summary>
    /// Return the kern value (FUnits) that applies at correction height
    /// <paramref name="height"/>. The step function is defined as:
    /// <c>kernValues[i]</c> for the smallest <c>i</c> with
    /// <c>height ≤ correctionHeights[i]</c>, and <c>kernValues[N]</c>
    /// when <c>height</c> exceeds every correction height.
    /// </summary>
    public short Lookup(short height)
    {
        for (var i = 0; i < _correctionHeights.Length; i++)
        {
            if (height <= _correctionHeights[i]) return _kernValues[i];
        }
        return _kernValues[_kernValues.Length - 1];
    }

    /// <summary>Number of correction heights — useful for tests.</summary>
    public int HeightCount => _correctionHeights.Length;

    internal static MathKern Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var heightCount = r.ReadUInt16();
        var heights = new short[heightCount];
        for (var i = 0; i < heightCount; i++)
        {
            heights[i] = r.ReadInt16();
            r.Skip(2); // device table offset, unused
        }
        var kerns = new short[heightCount + 1];
        for (var i = 0; i <= heightCount; i++)
        {
            kerns[i] = r.ReadInt16();
            r.Skip(2);
        }
        return new MathKern(heights, kerns);
    }
}

/// <summary>
/// The four corner kerns attached to a single math base glyph. Any of the
/// four may be null when the font supplies no data for that corner — a
/// common shape, since most letters only need top-right (super) and
/// bottom-right (sub) kerns. Top-left / bottom-left are populated for
/// pre-script (right-to-left math) and for stretchy operators.
/// </summary>
public sealed record MathKernInfoRecord(
    MathKern? TopRight,
    MathKern? TopLeft,
    MathKern? BottomRight,
    MathKern? BottomLeft);
