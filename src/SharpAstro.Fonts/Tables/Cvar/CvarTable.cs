using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cvar;

/// <summary>
/// CVT Variations table ('cvar'). Applies per-variation-axis deltas to the
/// Control Value Table (CVT) entries used by TrueType hinting. Uses the same
/// packed-tuple-variation format as 'gvar' but targets CVT indices rather than
/// glyph outline points.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/cvar</para>
/// </summary>
internal sealed class CvarTable
{
    // Parsed at Load time; each tuple covers a subset (or all) of CVT entries.
    private readonly CvarTuple[] _tuples;

    private CvarTable(CvarTuple[] tuples) => _tuples = tuples;

    /// <summary>
    /// Apply variation deltas to a pre-scaled CVT array in-place. Each entry
    /// in <paramref name="cvtF26"/> is in 26.6 fixed-point pixel units (as
    /// produced by the hinting pipeline). The deltas are already in CVT-entry
    /// index space — the per-entry sum of all contributing tuple deltas is
    /// rounded to the nearest integer and added.
    ///
    /// <para>When the font has no 'cvar' data this is a no-op.</para>
    /// </summary>
    public void ApplyDeltas(Span<int> cvtF26, ReadOnlySpan<float> normalizedCoords,
        float scale26_6)
    {
        foreach (var tuple in _tuples)
        {
            var scalar = tuple.ComputeScalar(normalizedCoords);
            if (scalar == 0f) continue;

            var deltas = tuple.Deltas;
            var points = tuple.CvtIndices;

            if (points is null)
            {
                // All CVT entries.
                var n = Math.Min(deltas.Length, cvtF26.Length);
                for (var i = 0; i < n; i++)
                    cvtF26[i] += (int)MathF.Round(deltas[i] * scalar * scale26_6);
            }
            else
            {
                // Specific CVT indices.
                for (var k = 0; k < points.Length && k < deltas.Length; k++)
                {
                    var idx = points[k];
                    if ((uint)idx < (uint)cvtF26.Length)
                        cvtF26[idx] += (int)MathF.Round(deltas[k] * scalar * scale26_6);
                }
            }
        }
    }

    /// <summary>
    /// Parse a 'cvar' table. Returns null and logs nothing if the data is
    /// too short to be valid.
    /// </summary>
    public static CvarTable? Parse(ReadOnlySpan<byte> data, ushort axisCount)
    {
        if (data.Length < 8) return null;

        var r = new BigEndianReader(data);
        var majorVersion = r.ReadUInt16(); // must be 1
        var minorVersion = r.ReadUInt16(); // must be 0
        var tupleVariationCount = r.ReadUInt16();
        var dataOffset = r.ReadUInt16(); // offset to serialized data from start of table

        var sharedPointNumbers = (tupleVariationCount & 0x8000) != 0;
        var count = tupleVariationCount & 0x0FFF;

        // Parse tuple headers (variationDataSize + tupleIndex, then optional
        // embeddedPeak / intermediateRegion coords).
        var headers = new TupleHeader[count];
        for (var i = 0; i < count; i++)
            headers[i] = ReadTupleHeader(ref r, axisCount);

        // Serialized data section (packed point numbers + packed deltas).
        if (dataOffset >= data.Length) return null;
        var dataSection = data[dataOffset..];

        // Shared point numbers (SHARED_POINT_NUMBERS flag).
        int[]? sharedPoints = null;
        if (sharedPointNumbers)
            sharedPoints = ReadPackedPointNumbers(ref dataSection);

        var tuples = new List<CvarTuple>(count);
        var dataPos = 0;

        for (var i = 0; i < count; i++)
        {
            var h = headers[i];
            if (dataPos + h.VariationDataSize > dataSection.Length) break;

            var tupleData = dataSection.Slice(dataPos, h.VariationDataSize);
            dataPos += h.VariationDataSize;

            int[]? cvtIndices;
            if (h.PrivatePointNumbers)
            {
                // Private point numbers are embedded at the start of this
                // tuple's serialized data section.
                cvtIndices = ReadPackedPointNumbers(ref tupleData);
            }
            else
            {
                cvtIndices = sharedPoints; // null = all CVT entries
            }

            // Number of deltas to read.
            // When cvtIndices is null the spec doesn't tell us how many
            // CVT entries exist here — we'll read as many as are encoded.
            var n = cvtIndices?.Length ?? int.MaxValue;
            var deltas = ReadPackedDeltas(ref tupleData, n);

            tuples.Add(new CvarTuple(h.Peak, h.Start, h.End, cvtIndices, deltas));
        }

        return new CvarTable([.. tuples]);
    }

    // ---- Tuple header -------------------------------------------------------

    private struct TupleHeader
    {
        public ushort VariationDataSize;
        public float[] Peak;
        public float[]? Start;
        public float[]? End;
        public bool PrivatePointNumbers;
    }

    private static TupleHeader ReadTupleHeader(ref BigEndianReader r, ushort axisCount)
    {
        var h = new TupleHeader();
        h.VariationDataSize = r.ReadUInt16();
        var tupleIndex = r.ReadUInt16();

        var embeddedPeak = (tupleIndex & 0x8000) != 0;
        var intermediateRegion = (tupleIndex & 0x4000) != 0;
        h.PrivatePointNumbers = (tupleIndex & 0x2000) != 0;

        if (embeddedPeak)
        {
            var peak = new float[axisCount];
            for (var i = 0; i < axisCount; i++) peak[i] = r.ReadF2Dot14();
            h.Peak = peak;
        }
        else
        {
            // cvar requires an embedded peak — treat as zero-length peak.
            h.Peak = [];
        }

        if (intermediateRegion)
        {
            var start = new float[axisCount];
            for (var i = 0; i < axisCount; i++) start[i] = r.ReadF2Dot14();
            var end = new float[axisCount];
            for (var i = 0; i < axisCount; i++) end[i] = r.ReadF2Dot14();
            h.Start = start;
            h.End = end;
        }
        return h;
    }

    // ---- Packed point-number decoding (same algorithm as gvar) --------------

    private static int[]? ReadPackedPointNumbers(ref ReadOnlySpan<byte> data)
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
        if (count == 0) return null; // all CVT entries

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
        return result;
    }

    // ---- Packed delta decoding (same algorithm as gvar) ---------------------

    private static short[] ReadPackedDeltas(ref ReadOnlySpan<byte> data, int maxCount)
    {
        var result = new List<short>();
        var emitted = 0;
        while (emitted < maxCount && data.Length > 0)
        {
            var control = data[0];
            data = data[1..];
            var runLen = (control & 0x3F) + 1;
            var isZero = (control & 0x80) != 0;
            var isWord = (control & 0x40) != 0;
            if (isZero)
            {
                for (var i = 0; i < runLen && emitted < maxCount; i++)
                {
                    result.Add(0);
                    emitted++;
                }
            }
            else if (isWord)
            {
                if (data.Length < runLen * 2) break;
                for (var i = 0; i < runLen && emitted < maxCount; i++)
                {
                    result.Add((short)((data[i * 2] << 8) | data[i * 2 + 1]));
                    emitted++;
                }
                data = data[(runLen * 2)..];
            }
            else
            {
                if (data.Length < runLen) break;
                for (var i = 0; i < runLen && emitted < maxCount; i++)
                {
                    result.Add((sbyte)data[i]);
                    emitted++;
                }
                data = data[runLen..];
            }
        }
        return [.. result];
    }
}

/// <summary>
/// One parsed CVT variation tuple: a scalar region (peak ± start/end) and a
/// set of CVT index → delta pairs.
/// </summary>
internal sealed class CvarTuple
{
    /// <summary>Per-axis peak coordinates (F2.14, normalized).</summary>
    public float[] Peak { get; }
    /// <summary>Intermediate-region start; null = inferred from peak.</summary>
    public float[]? Start { get; }
    /// <summary>Intermediate-region end; null = inferred from peak.</summary>
    public float[]? End { get; }
    /// <summary>CVT entry indices this tuple applies to; null = all entries.</summary>
    public int[]? CvtIndices { get; }
    /// <summary>Deltas parallel to <see cref="CvtIndices"/> (or all CVT entries).</summary>
    public short[] Deltas { get; }

    public CvarTuple(float[] peak, float[]? start, float[]? end,
        int[]? cvtIndices, short[] deltas)
    {
        Peak = peak;
        Start = start;
        End = end;
        CvtIndices = cvtIndices;
        Deltas = deltas;
    }

    /// <summary>
    /// Compute the scalar weight for this tuple at the given normalized
    /// axis coordinates. Returns 0 when the coordinates are outside the
    /// region (no contribution) or 1 at the peak (full contribution).
    /// </summary>
    public float ComputeScalar(ReadOnlySpan<float> normCoords)
    {
        var s = 1f;
        for (var i = 0; i < Peak.Length && i < normCoords.Length; i++)
        {
            var v = normCoords[i];
            var peak = Peak[i];
            if (peak == 0f) continue; // neutral axis — no contribution from this axis

            float start, end;
            if (Start is not null && End is not null)
            {
                start = Start[i];
                end = End[i];
            }
            else
            {
                // Infer intermediate region from peak (same rule as gvar).
                start = peak < 0f ? peak : 0f;
                end = peak > 0f ? peak : 0f;
            }

            if (v == peak) continue;            // exactly at peak
            if (v <= start || v >= end) return 0f; // outside region
            if (v < peak)
                s *= (v - start) / (peak - start);
            else
                s *= (end - v) / (end - peak);
        }
        return s;
    }
}
