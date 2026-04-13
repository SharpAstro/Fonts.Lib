using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Gvar;

/// <summary>
/// One TrueType variation tuple — a delta set to add to a glyph's points
/// when the variation coordinate falls within (start, end) with a peak at
/// <see cref="Peak"/>.
/// </summary>
public sealed class TupleVariation
{
    /// <summary>Per-axis peak coordinate (length = axisCount).</summary>
    public float[] Peak { get; }
    /// <summary>Intermediate region start (null = take from peak with 0 fallback).</summary>
    public float[]? Start { get; }
    public float[]? End { get; }

    /// <summary>
    /// Affected point indices, OR null = "all points" (the common case where
    /// every outline point gets a delta). When non-null, only these points
    /// are explicitly moved; others are interpolated via IUP.
    /// </summary>
    public int[]? PointNumbers { get; }

    /// <summary>X-deltas, length = either PointNumbers.Length or pointCount + 4 (with phantoms).</summary>
    public short[] DeltaX { get; }
    /// <summary>Y-deltas, same length as DeltaX.</summary>
    public short[] DeltaY { get; }

    public TupleVariation(float[] peak, float[]? start, float[]? end,
        int[]? pointNumbers, short[] dx, short[] dy)
    {
        Peak = peak;
        Start = start;
        End = end;
        PointNumbers = pointNumbers;
        DeltaX = dx;
        DeltaY = dy;
    }

    /// <summary>
    /// Compute the scalar weight for this tuple given the current normalized
    /// variation coordinate vector. 0 = no contribution; 1 = full delta.
    /// </summary>
    public float ComputeScalar(ReadOnlySpan<float> normCoords)
    {
        var s = 1f;
        for (var i = 0; i < Peak.Length && i < normCoords.Length; i++)
        {
            var v = normCoords[i];
            var peak = Peak[i];
            // Axis with peak == 0 is "any value" — no contribution.
            if (peak == 0) continue;
            // Intermediate region (start, end) explicit; else inferred from peak.
            float start, end;
            if (Start is not null && End is not null)
            {
                start = Start[i];
                end = End[i];
            }
            else
            {
                start = peak < 0 ? peak : 0;
                end = peak > 0 ? peak : 0;
            }

            if (v == peak) continue;     // exactly at peak: full effect
            if (v <= start || v >= end) return 0f; // outside the region
            if (v < peak)
                s *= (v - start) / (peak - start);
            else
                s *= (end - v) / (end - peak);
        }
        return s;
    }
}

/// <summary>
/// Parsed 'gvar' table — per-glyph TrueType outline variation deltas.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/gvar
///
/// <para>Per-glyph data is decoded lazily via <see cref="LoadGlyphTuples"/>.</para>
/// </summary>
public sealed class GvarTable
{
    private readonly ReadOnlyMemory<byte> _table;
    private readonly uint[] _glyphDataOffsets; // length = glyphCount + 1; absolute within _table
    private readonly float[][] _sharedTuples;  // sharedTuples[i] = float[axisCount]
    public ushort AxisCount { get; }

    private GvarTable(ReadOnlyMemory<byte> table, uint[] offsets,
        float[][] sharedTuples, ushort axisCount)
    {
        _table = table;
        _glyphDataOffsets = offsets;
        _sharedTuples = sharedTuples;
        AxisCount = axisCount;
    }

    public bool HasDataForGlyph(uint gid)
        => gid + 1 < _glyphDataOffsets.Length
           && _glyphDataOffsets[gid + 1] > _glyphDataOffsets[gid];

    public static GvarTable Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var r = new BigEndianReader(span);
        // majorVersion + minorVersion
        r.Skip(4);
        var axisCount = r.ReadUInt16();
        var sharedTupleCount = r.ReadUInt16();
        var sharedTuplesOffset = r.ReadUInt32();
        var glyphCount = r.ReadUInt16();
        var flags = r.ReadUInt16();
        var glyphVariationDataArrayOffset = r.ReadUInt32();

        var longOffsets = (flags & 1) != 0;
        var offsets = new uint[glyphCount + 1];
        if (longOffsets)
        {
            for (var i = 0; i <= glyphCount; i++)
                offsets[i] = r.ReadUInt32() + glyphVariationDataArrayOffset;
        }
        else
        {
            for (var i = 0; i <= glyphCount; i++)
                offsets[i] = (uint)r.ReadUInt16() * 2u + glyphVariationDataArrayOffset;
        }

        var sharedTuples = new float[sharedTupleCount][];
        var sr = new BigEndianReader(span, (int)sharedTuplesOffset);
        for (var i = 0; i < sharedTupleCount; i++)
        {
            var t = new float[axisCount];
            for (var k = 0; k < axisCount; k++) t[k] = sr.ReadF2Dot14();
            sharedTuples[i] = t;
        }

        return new GvarTable(data, offsets, sharedTuples, axisCount);
    }

    /// <summary>
    /// Decode all tuple variations for one glyph. <paramref name="pointCount"/>
    /// is the glyph's outline point count (without phantom points); we need
    /// it to know how many deltas to read when the tuple uses "all points".
    /// </summary>
    public List<TupleVariation> LoadGlyphTuples(uint gid, int pointCount)
    {
        var result = new List<TupleVariation>(8);
        if (gid + 1 >= _glyphDataOffsets.Length) return result;
        var start = _glyphDataOffsets[gid];
        var end = _glyphDataOffsets[gid + 1];
        if (end <= start) return result;

        var span = _table.Span;
        var glyphData = span.Slice((int)start, (int)(end - start));
        var r = new BigEndianReader(glyphData);

        var tupleVariationCount = r.ReadUInt16();
        var sharedPointNumbers = (tupleVariationCount & 0x8000) != 0;
        var count = tupleVariationCount & 0x0FFF;
        var dataOffset = r.ReadUInt16(); // relative to start of glyphData

        // Parse tuple headers.
        var headers = new TupleHeader[count];
        for (var i = 0; i < count; i++)
            headers[i] = ReadTupleHeader(ref r);

        // Now serialized data section.
        var dataSection = glyphData[dataOffset..];

        int[]? sharedPoints = null;
        if (sharedPointNumbers)
        {
            sharedPoints = ReadPackedPointNumbers(ref dataSection, pointCount + 4);
        }

        var dataPos = 0;
        for (var i = 0; i < count; i++)
        {
            var h = headers[i];
            var tupleData = dataSection.Slice(dataPos, h.VariationDataSize);
            dataPos += h.VariationDataSize;

            int[]? points;
            if (h.PrivatePointNumbers)
            {
                var s = tupleData;
                points = ReadPackedPointNumbers(ref s, pointCount + 4);
                tupleData = s; // s was advanced by ReadPackedPointNumbers
            }
            else
            {
                points = sharedPoints; // null = all points
            }

            var n = points?.Length ?? (pointCount + 4);
            var dx = ReadPackedDeltas(ref tupleData, n);
            var dy = ReadPackedDeltas(ref tupleData, n);

            // Determine peak.
            float[] peak;
            if (h.EmbeddedPeak is not null) peak = h.EmbeddedPeak;
            else if (h.SharedTupleIndex < _sharedTuples.Length)
                peak = _sharedTuples[h.SharedTupleIndex];
            else continue; // malformed; skip

            result.Add(new TupleVariation(peak, h.Start, h.End, points, dx, dy));
        }
        return result;
    }

    // ---- Header / packed-data parsing -------------------------------------

    private struct TupleHeader
    {
        public ushort VariationDataSize;
        public bool EmbeddedPeak_;
        public float[]? EmbeddedPeak;
        public float[]? Start;
        public float[]? End;
        public bool PrivatePointNumbers;
        public int SharedTupleIndex;
    }

    private TupleHeader ReadTupleHeader(ref BigEndianReader r)
    {
        var h = new TupleHeader();
        h.VariationDataSize = r.ReadUInt16();
        var tupleIndex = r.ReadUInt16();
        var embeddedPeak = (tupleIndex & 0x8000) != 0;
        var intermediateRegion = (tupleIndex & 0x4000) != 0;
        h.PrivatePointNumbers = (tupleIndex & 0x2000) != 0;
        h.SharedTupleIndex = tupleIndex & 0x0FFF;

        if (embeddedPeak)
        {
            h.EmbeddedPeak_ = true;
            var peak = new float[AxisCount];
            for (var i = 0; i < AxisCount; i++) peak[i] = r.ReadF2Dot14();
            h.EmbeddedPeak = peak;
        }
        if (intermediateRegion)
        {
            var start = new float[AxisCount];
            for (var i = 0; i < AxisCount; i++) start[i] = r.ReadF2Dot14();
            var end = new float[AxisCount];
            for (var i = 0; i < AxisCount; i++) end[i] = r.ReadF2Dot14();
            h.Start = start;
            h.End = end;
        }
        return h;
    }

    /// <summary>
    /// Decode a packed point-numbers run. Returns null when count == 0 (= "all points").
    /// Otherwise an int[] of point numbers (length = count) where each entry
    /// is delta-decoded from the previous.
    /// Consumes bytes from <paramref name="data"/>.
    /// </summary>
    private static int[]? ReadPackedPointNumbers(ref ReadOnlySpan<byte> data, int totalPointCount)
    {
        if (data.Length == 0) return null;
        int countByte = data[0];
        int count;
        int headerLen;
        if (countByte < 0x80)
        {
            count = countByte;
            headerLen = 1;
        }
        else
        {
            if (data.Length < 2) return null;
            count = ((countByte & 0x7F) << 8) | data[1];
            headerLen = 2;
        }
        data = data[headerLen..];
        if (count == 0) return null; // all points

        var result = new int[count];
        var prev = 0;
        var emitted = 0;
        while (emitted < count)
        {
            if (data.Length == 0) break;
            var control = data[0];
            data = data[1..];
            var runLen = (control & 0x7F) + 1;
            var isWord = (control & 0x80) != 0;
            var bytesPerEntry = isWord ? 2 : 1;
            if (data.Length < runLen * bytesPerEntry) break;
            for (var i = 0; i < runLen && emitted < count; i++)
            {
                int delta = isWord
                    ? (data[i * 2] << 8) | data[i * 2 + 1]
                    : data[i];
                prev += delta;
                result[emitted++] = prev;
            }
            data = data[(runLen * bytesPerEntry)..];
        }
        _ = totalPointCount; // could validate prev <= totalPointCount
        return result;
    }

    private static short[] ReadPackedDeltas(ref ReadOnlySpan<byte> data, int count)
    {
        var result = new short[count];
        var emitted = 0;
        while (emitted < count)
        {
            if (data.Length == 0) break;
            var control = data[0];
            data = data[1..];
            var runLen = (control & 0x3F) + 1;
            var isZero = (control & 0x80) != 0;
            var isWord = (control & 0x40) != 0;
            if (isZero)
            {
                for (var i = 0; i < runLen && emitted < count; i++)
                    result[emitted++] = 0;
            }
            else if (isWord)
            {
                if (data.Length < runLen * 2) break;
                for (var i = 0; i < runLen && emitted < count; i++)
                    result[emitted++] = (short)((data[i * 2] << 8) | data[i * 2 + 1]);
                data = data[(runLen * 2)..];
            }
            else
            {
                if (data.Length < runLen) break;
                for (var i = 0; i < runLen && emitted < count; i++)
                    result[emitted++] = (sbyte)data[i];
                data = data[runLen..];
            }
        }
        return result;
    }
}
