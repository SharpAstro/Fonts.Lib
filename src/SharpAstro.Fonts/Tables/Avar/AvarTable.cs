using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Avar;

/// <summary>
/// Parsed 'avar' (Axis Variations) table — piecewise-linear remap of
/// normalized axis coordinates. Each axis has its own segment map.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/avar</para>
/// </summary>
public sealed class AvarTable
{
    /// <summary>One segment map per axis (in the same order as fvar).</summary>
    public AvarSegmentMap[] SegmentMaps { get; }

    private AvarTable(AvarSegmentMap[] maps) => SegmentMaps = maps;

    /// <summary>
    /// Apply this table's per-axis remap to the given normalized coordinates.
    /// Coordinates outside the [-1, 1] range or for axes without a non-trivial
    /// map pass through unchanged.
    /// </summary>
    public void Apply(Span<float> normalizedCoords)
    {
        var n = Math.Min(normalizedCoords.Length, SegmentMaps.Length);
        for (var i = 0; i < n; i++)
            normalizedCoords[i] = SegmentMaps[i].Remap(normalizedCoords[i]);
    }

    public static AvarTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // majorVersion + minorVersion + reserved
        r.Skip(6);
        var axisCount = r.ReadUInt16();
        var maps = new AvarSegmentMap[axisCount];
        for (var i = 0; i < axisCount; i++)
        {
            var n = r.ReadUInt16();
            var pairs = new (float From, float To)[n];
            for (var k = 0; k < n; k++)
            {
                var from = r.ReadF2Dot14();
                var to = r.ReadF2Dot14();
                pairs[k] = (from, to);
            }
            maps[i] = new AvarSegmentMap(pairs);
        }
        return new AvarTable(maps);
    }
}

/// <summary>
/// Piecewise-linear segment map for a single axis. Pairs are sorted by
/// "from" coordinate; lookup is linear interpolation within the spanning
/// segment.
/// </summary>
public sealed class AvarSegmentMap
{
    private readonly (float From, float To)[] _pairs;
    public AvarSegmentMap((float From, float To)[] pairs) => _pairs = pairs;

    public float Remap(float value)
    {
        if (_pairs.Length == 0) return value;
        if (value <= _pairs[0].From) return _pairs[0].To;
        if (value >= _pairs[^1].From) return _pairs[^1].To;
        for (var i = 1; i < _pairs.Length; i++)
        {
            if (value <= _pairs[i].From)
            {
                var (fa, ta) = _pairs[i - 1];
                var (fb, tb) = _pairs[i];
                var span = fb - fa;
                if (span <= 0) return ta;
                return ta + (tb - ta) * (value - fa) / span;
            }
        }
        return _pairs[^1].To;
    }
}
