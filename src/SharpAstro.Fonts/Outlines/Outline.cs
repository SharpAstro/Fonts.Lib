using System.Collections.Immutable;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// One outline point in design (font) units. <see cref="OnCurve"/> = false
/// indicates a quadratic Bézier control point.
/// </summary>
public readonly record struct OutlinePoint(short X, short Y, bool OnCurve);

/// <summary>
/// A single glyph outline. Storage is packed into four
/// <see cref="ImmutableArray{T}"/> fields — compile-time immutability
/// guarantees with zero-overhead span access via <c>.AsSpan()</c>.
///
/// Coordinates are in design (font) units; for composite glyphs they are the
/// post-transform values rounded to int16 (matches FreeType <c>NO_SCALE</c>
/// behavior).
///
/// Instances are immutable and safe to share across threads.
/// </summary>
public sealed class Outline
{
    private readonly ImmutableArray<short> _x;
    private readonly ImmutableArray<short> _y;
    private readonly ImmutableArray<byte> _flags;       // bit 0: on-curve
    private readonly ImmutableArray<int> _contourEnds;  // inclusive end indices

    public Outline(ImmutableArray<short> x, ImmutableArray<short> y,
        ImmutableArray<byte> flags, ImmutableArray<int> contourEnds,
        (short XMin, short YMin, short XMax, short YMax) bounds,
        byte[]? instructions = null)
    {
        if (x.Length != y.Length || x.Length != flags.Length)
            throw new ArgumentException("Outline arrays must have equal length.");
        _x = x;
        _y = y;
        _flags = flags;
        _contourEnds = contourEnds;
        Bounds = bounds;
        Instructions = instructions;
    }

    public static readonly Outline Empty = new(
        ImmutableArray<short>.Empty, ImmutableArray<short>.Empty,
        ImmutableArray<byte>.Empty, ImmutableArray<int>.Empty,
        (0, 0, 0, 0));

    /// <summary>
    /// TrueType bytecode instructions for this glyph (Phase 8). Null if the
    /// glyph carries no hinting program (CFF outlines, fonts without 'fpgm',
    /// or simple glyphs with instructionLength == 0). Composite glyphs only
    /// surface instructions when the WE_HAVE_INSTRUCTIONS flag is set on the
    /// composite header.
    /// </summary>
    public byte[]? Instructions { get; }

    public bool IsEmpty => _contourEnds.IsEmpty;
    public int PointCount => _x.Length;
    public int ContourCount => _contourEnds.Length;
    public (short XMin, short YMin, short XMax, short YMax) Bounds { get; }

    /// <summary>Read-only view of the X coordinates (design units).</summary>
    public ReadOnlySpan<short> X => _x.AsSpan();
    /// <summary>Read-only view of the Y coordinates (design units).</summary>
    public ReadOnlySpan<short> Y => _y.AsSpan();
    /// <summary>Per-point flags. Bit 0 = on-curve.</summary>
    public ReadOnlySpan<byte> Flags => _flags.AsSpan();
    /// <summary>Inclusive end indices for each contour.</summary>
    public ReadOnlySpan<int> ContourEnds => _contourEnds.AsSpan();

    /// <summary>Zero-copy immutable flags for sharing between Outline instances
    /// (e.g. variation pipeline reuses the same flags when only X/Y change).</summary>
    public ImmutableArray<byte> FlagsImmutable => _flags;
    /// <summary>Zero-copy immutable contour ends for sharing between Outline
    /// instances and the hinting pipeline.</summary>
    public ImmutableArray<int> ContourEndsImmutable => _contourEnds;

    public bool IsOnCurve(int pointIndex) => (_flags[pointIndex] & 1) != 0;

    public OutlinePoint GetPoint(int pointIndex)
        => new(_x[pointIndex], _y[pointIndex], IsOnCurve(pointIndex));

    /// <summary>Iterate the (start, end) inclusive index range of each contour.</summary>
    public IEnumerable<(int Start, int End)> ContourRanges()
    {
        var start = 0;
        for (var i = 0; i < _contourEnds.Length; i++)
        {
            yield return (start, _contourEnds[i]);
            start = _contourEnds[i] + 1;
        }
    }
}

/// <summary>
/// Receives outline path commands without allocating a full <see cref="Outline"/>.
/// Hot-path API for rasterizers and other consumers. Implementations are not
/// expected to be thread-safe (one sink per call).
///
/// <para>TrueType outlines emit only <see cref="QuadTo"/> (quadratic);
/// CFF/Type-2 charstrings emit only <see cref="CubicTo"/> (cubic). Sinks
/// that consume both formats must implement both methods.</para>
/// </summary>
public interface IGlyphSink
{
    void MoveTo(float x, float y);
    void LineTo(float x, float y);
    void QuadTo(float cx, float cy, float x, float y);
    void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y);
    /// <summary>Called when the current contour closes back to its start.</summary>
    void Close();
}
