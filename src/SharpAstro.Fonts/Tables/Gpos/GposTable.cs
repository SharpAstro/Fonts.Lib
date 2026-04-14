using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Gpos;

/// <summary>
/// OpenType GPOS table — General Positioning. This implementation parses only
/// LookupType 2 (PairAdjustment), which is the GPOS equivalent of the legacy
/// 'kern' table. ScriptList and FeatureList are intentionally not parsed; we
/// scan all type-2 lookups unconditionally, matching the behaviour of most
/// simple renderers.
///
/// <para>Spec: https://learn.microsoft.com/en-us/typography/opentype/spec/gpos</para>
/// </summary>
internal sealed class GposTable
{
    // -----------------------------------------------------------------
    // Internal data model — one entry per type-2 subtable, union-discriminated
    // by _format (1 = pair sets, 2 = class-based).
    // -----------------------------------------------------------------

    /// <summary>
    /// PairAdjustment Format 1: per-glyph pair sets.
    /// coverageGlyphs[i] → pairs sorted by secondGlyph for binary search.
    /// </summary>
    private sealed class Format1Subtable
    {
        /// <summary>Glyph IDs covered (parallel to <see cref="PairSets"/>).</summary>
        internal readonly ushort[] CoverageGlyphs;
        /// <summary>For each covered glyph: sorted array of (secondGlyph, xAdvance1).</summary>
        internal readonly (ushort second, short xAdv1)[][] PairSets;

        internal Format1Subtable(ushort[] coverageGlyphs, (ushort second, short xAdv1)[][] pairSets)
        {
            CoverageGlyphs = coverageGlyphs;
            PairSets = pairSets;
        }

        internal int GetAdjustment(uint leftGid, uint rightGid)
        {
            // Find left glyph in coverage.
            var covIdx = FindCoverageIndex(CoverageGlyphs, leftGid);
            if (covIdx < 0) return 0;

            // Binary-search the pair set for the right glyph.
            var set = PairSets[covIdx];
            int lo = 0, hi = set.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var cmp = set[mid].second.CompareTo((ushort)rightGid);
                if (cmp == 0) return set[mid].xAdv1;
                if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return 0;
        }
    }

    /// <summary>
    /// PairAdjustment Format 2: class-based pair matrix.
    /// Class1Record[class1][class2] → xAdvance1.
    /// </summary>
    private sealed class Format2Subtable
    {
        /// <summary>Covered first-glyph IDs (used for a quick membership check).</summary>
        internal readonly ushort[] CoverageGlyphs;
        /// <summary>ClassDef1: maps firstGlyph → class1 index.</summary>
        internal readonly ClassDef ClassDef1;
        /// <summary>ClassDef2: maps secondGlyph → class2 index.</summary>
        internal readonly ClassDef ClassDef2;
        /// <summary>Matrix[class1][class2] → xAdvance1 in FUnits.</summary>
        internal readonly short[][] Matrix;
        internal readonly int Class2Count;

        internal Format2Subtable(ushort[] coverageGlyphs, ClassDef classDef1, ClassDef classDef2,
            short[][] matrix, int class2Count)
        {
            CoverageGlyphs = coverageGlyphs;
            ClassDef1 = classDef1;
            ClassDef2 = classDef2;
            Matrix = matrix;
            Class2Count = class2Count;
        }

        internal int GetAdjustment(uint leftGid, uint rightGid)
        {
            // Quick membership check: left glyph must be in coverage.
            if (FindCoverageIndex(CoverageGlyphs, leftGid) < 0) return 0;

            var c1 = ClassDef1.GetClass(leftGid);
            var c2 = ClassDef2.GetClass(rightGid);
            if (c1 >= Matrix.Length || c2 >= Class2Count) return 0;
            return Matrix[c1][c2];
        }
    }

    // -----------------------------------------------------------------
    // ClassDef — shared by Format 2 subtables.
    // -----------------------------------------------------------------

    /// <summary>
    /// OpenType ClassDef table (format 1: array, format 2: ranges).
    /// Glyph IDs not listed return class 0.
    /// </summary>
    private sealed class ClassDef
    {
        // Format 1: startGlyph + sequential array.
        private readonly ushort _startGlyph;
        private readonly ushort[]? _classArray;  // non-null ↔ format 1

        // Format 2: sorted class-range records.
        private readonly (ushort start, ushort end, ushort cls)[]? _ranges; // non-null ↔ format 2

        private ClassDef(ushort startGlyph, ushort[] classArray)
        {
            _startGlyph = startGlyph;
            _classArray = classArray;
        }

        private ClassDef((ushort start, ushort end, ushort cls)[] ranges)
        {
            _ranges = ranges;
        }

        public int GetClass(uint glyphId)
        {
            if (_classArray is not null)
            {
                // Format 1: sequential array starting at _startGlyph.
                var idx = (int)glyphId - _startGlyph;
                if (idx < 0 || idx >= _classArray.Length) return 0;
                return _classArray[idx];
            }

            // Format 2: binary search over ranges.
            var ranges = _ranges!;
            int lo = 0, hi = ranges.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var r = ranges[mid];
                if (glyphId < r.start) hi = mid - 1;
                else if (glyphId > r.end) lo = mid + 1;
                else return r.cls;
            }
            return 0;
        }

        public static ClassDef Parse(ReadOnlySpan<byte> tableBase, int offset)
        {
            if (offset <= 0 || offset >= tableBase.Length) return Empty;
            var r = new BigEndianReader(tableBase[offset..]);
            var format = r.ReadUInt16();

            if (format == 1)
            {
                var startGlyph = r.ReadUInt16();
                var count = r.ReadUInt16();
                if (count == 0) return Empty;
                var arr = new ushort[count];
                for (var i = 0; i < count; i++) arr[i] = r.ReadUInt16();
                return new ClassDef(startGlyph, arr);
            }

            if (format == 2)
            {
                var rangeCount = r.ReadUInt16();
                if (rangeCount == 0) return Empty;
                var ranges = new (ushort start, ushort end, ushort cls)[rangeCount];
                for (var i = 0; i < rangeCount; i++)
                    ranges[i] = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
                return new ClassDef(ranges);
            }

            // Unknown format — return empty (all class 0).
            return Empty;
        }

        /// <summary>Singleton empty ClassDef — every glyph maps to class 0.</summary>
        public static readonly ClassDef Empty = new ClassDef(Array.Empty<(ushort, ushort, ushort)>());
    }

    // -----------------------------------------------------------------
    // GposTable state.
    // -----------------------------------------------------------------

    private readonly Format1Subtable[] _format1;
    private readonly Format2Subtable[] _format2;

    private GposTable(Format1Subtable[] format1, Format2Subtable[] format2)
    {
        _format1 = format1;
        _format2 = format2;
    }

    /// <summary>
    /// Get the X-advance kerning adjustment (in FUnits) for the glyph pair
    /// (<paramref name="leftGid"/>, <paramref name="rightGid"/>).
    /// Returns 0 when no pair adjustment is defined.
    /// </summary>
    public int GetPairAdjustment(uint leftGid, uint rightGid)
    {
        // Format 1 first (per-glyph pairs), then format 2 (class-based).
        foreach (var sub in _format1)
        {
            var v = sub.GetAdjustment(leftGid, rightGid);
            if (v != 0) return v;
        }
        foreach (var sub in _format2)
        {
            var v = sub.GetAdjustment(leftGid, rightGid);
            if (v != 0) return v;
        }
        return 0;
    }

    // -----------------------------------------------------------------
    // Parsing helpers.
    // -----------------------------------------------------------------

    /// <summary>
    /// Binary-search a sorted coverage glyph array (format 1 coverage, flattened
    /// from either coverage format). Returns the coverage index, or −1.
    /// </summary>
    private static int FindCoverageIndex(ushort[] glyphs, uint glyphId)
    {
        int lo = 0, hi = glyphs.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var cmp = glyphs[mid].CompareTo((ushort)glyphId);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>
    /// Parse a CoverageTable and return a sorted array of covered glyph IDs.
    /// The returned array is always sorted ascending, enabling binary search.
    /// </summary>
    private static ushort[] ParseCoverage(ReadOnlySpan<byte> tableBase, int offset)
    {
        if (offset <= 0 || offset >= tableBase.Length) return [];
        var r = new BigEndianReader(tableBase[offset..]);
        var format = r.ReadUInt16();

        if (format == 1)
        {
            // Format 1: explicit glyph list (already sorted per spec).
            var count = r.ReadUInt16();
            if (count == 0) return [];
            var glyphs = new ushort[count];
            for (var i = 0; i < count; i++) glyphs[i] = r.ReadUInt16();
            return glyphs;
        }

        if (format == 2)
        {
            // Format 2: ranges of glyph IDs.
            var rangeCount = r.ReadUInt16();
            if (rangeCount == 0) return [];

            // First pass: count total glyphs.
            var startPos = r.Position;
            var total = 0;
            for (var i = 0; i < rangeCount; i++)
            {
                var start = r.ReadUInt16();
                var end   = r.ReadUInt16();
                r.Skip(2); // startCoverageIndex — not needed; we rebuild indices.
                total += (end - start + 1);
            }

            // Second pass: fill array.
            r.Position = startPos;
            var glyphs = new ushort[total];
            var idx = 0;
            for (var i = 0; i < rangeCount; i++)
            {
                var start = r.ReadUInt16();
                var end   = r.ReadUInt16();
                r.Skip(2);
                for (var g = start; g <= end; g++)
                    glyphs[idx++] = (ushort)g;
            }
            return glyphs;
        }

        return [];
    }

    /// <summary>
    /// Determine the number of int16 fields to skip per ValueRecord based on
    /// the ValueFormat bitmask. Only bit 2 (XAdvance) carries kerning; other
    /// fields are skipped.
    /// </summary>
    private static int ValueRecordSize(ushort valueFormat)
    {
        // Each set bit = one int16 field (2 bytes).
        var bits = 0;
        var v = valueFormat;
        while (v != 0) { bits += v & 1; v >>= 1; }
        return bits * 2;
    }

    /// <summary>
    /// Read a ValueRecord and return the XAdvance field (bit 2 of valueFormat).
    /// Other fields are read and discarded so the reader position advances past
    /// the full record.
    /// </summary>
    private static short ReadValueRecord(ref BigEndianReader r, ushort valueFormat)
    {
        // Bit 0 = XPlacement, 1 = YPlacement, 2 = XAdvance, 3 = YAdvance,
        // 4..7 = device table offsets for each.
        short xAdv = 0;
        if ((valueFormat & 0x01) != 0) r.Skip(2); // XPlacement
        if ((valueFormat & 0x02) != 0) r.Skip(2); // YPlacement
        if ((valueFormat & 0x04) != 0) xAdv = r.ReadInt16(); // XAdvance
        if ((valueFormat & 0x08) != 0) r.Skip(2); // YAdvance
        if ((valueFormat & 0x10) != 0) r.Skip(2); // XPlaDevice
        if ((valueFormat & 0x20) != 0) r.Skip(2); // YPlaDevice
        if ((valueFormat & 0x40) != 0) r.Skip(2); // XAdvDevice
        if ((valueFormat & 0x80) != 0) r.Skip(2); // YAdvDevice
        return xAdv;
    }

    // -----------------------------------------------------------------
    // Public factory.
    // -----------------------------------------------------------------

    /// <summary>Parse a GPOS table. Returns null on unrecognised versions or
    /// malformed data rather than throwing — bad GPOS is non-fatal.</summary>
    public static GposTable? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) return null;
        var r = new BigEndianReader(data);

        var majorVersion = r.ReadUInt16(); // 1
        var minorVersion = r.ReadUInt16(); // 0 or 1
        if (majorVersion != 1) return null;

        r.Skip(2); // scriptListOffset — not used
        r.Skip(2); // featureListOffset — not used
        var lookupListOffset = r.ReadUInt16();

        if (lookupListOffset == 0 || lookupListOffset >= data.Length) return null;

        var format1List = new List<Format1Subtable>();
        var format2List = new List<Format2Subtable>();

        // Parse LookupList.
        var llr = new BigEndianReader(data[lookupListOffset..]);
        var lookupCount = llr.ReadUInt16();

        var lookupOffsets = new ushort[lookupCount];
        for (var i = 0; i < lookupCount; i++)
            lookupOffsets[i] = llr.ReadUInt16();

        for (var i = 0; i < lookupCount; i++)
        {
            var lookupBase = lookupListOffset + lookupOffsets[i];
            if (lookupBase >= data.Length) continue;

            var lr = new BigEndianReader(data[lookupBase..]);
            var lookupType    = lr.ReadUInt16();
            var lookupFlag    = lr.ReadUInt16();
            var subTableCount = lr.ReadUInt16();

            // Only PairAdjustment (type 2).
            if (lookupType != 2)
                continue;

            var subOffsets = new ushort[subTableCount];
            for (var s = 0; s < subTableCount; s++)
                subOffsets[s] = lr.ReadUInt16();

            for (var s = 0; s < subTableCount; s++)
            {
                var subBase = lookupBase + subOffsets[s];
                if (subBase >= data.Length) continue;

                ParsePairAdjustmentSubtable(data, subBase, format1List, format2List);
            }
        }

        return new GposTable(format1List.ToArray(), format2List.ToArray());
    }

    private static void ParsePairAdjustmentSubtable(
        ReadOnlySpan<byte> data, int subBase,
        List<Format1Subtable> format1List,
        List<Format2Subtable> format2List)
    {
        var r = new BigEndianReader(data[subBase..]);
        var posFormat = r.ReadUInt16();

        if (posFormat == 1)
        {
            // PairAdjustment Format 1: individual pair sets.
            var coverageOffset = r.ReadUInt16();
            var valueFormat1   = r.ReadUInt16();
            var valueFormat2   = r.ReadUInt16();
            var pairSetCount   = r.ReadUInt16();

            // Collect pair set offsets before we start slicing.
            var pairSetOffsets = new ushort[pairSetCount];
            for (var i = 0; i < pairSetCount; i++)
                pairSetOffsets[i] = r.ReadUInt16();

            var coverage = ParseCoverage(data[subBase..], coverageOffset);
            if (coverage.Length == 0) return;

            var vr1Size = ValueRecordSize(valueFormat1);
            var vr2Size = ValueRecordSize(valueFormat2);

            var pairSets = new (ushort second, short xAdv1)[pairSetCount][];
            for (var i = 0; i < pairSetCount; i++)
            {
                var psBase = subBase + pairSetOffsets[i];
                if (psBase >= data.Length) { pairSets[i] = []; continue; }

                var pr = new BigEndianReader(data[psBase..]);
                var pairCount = pr.ReadUInt16();
                var pairs = new (ushort second, short xAdv1)[pairCount];

                for (var p = 0; p < pairCount; p++)
                {
                    var secondGlyph = pr.ReadUInt16();
                    // Read valueRecord1 — we only want XAdvance.
                    var xAdv1 = ReadValueRecord(ref pr, valueFormat1);
                    // Read valueRecord2 — we discard it (affects the right glyph's position).
                    ReadValueRecord(ref pr, valueFormat2);
                    pairs[p] = (secondGlyph, xAdv1);
                }

                // Spec requires pairs to be sorted by secondGlyph; sort anyway for safety.
                Array.Sort(pairs, (a, b) => a.second.CompareTo(b.second));
                pairSets[i] = pairs;
            }

            // Only add subtable when it has at least one covered glyph pair set.
            if (coverage.Length > 0 && pairSetCount > 0)
                format1List.Add(new Format1Subtable(coverage, pairSets));
        }
        else if (posFormat == 2)
        {
            // PairAdjustment Format 2: class-based.
            var coverageOffset  = r.ReadUInt16();
            var valueFormat1    = r.ReadUInt16();
            var valueFormat2    = r.ReadUInt16();
            var classDef1Offset = r.ReadUInt16();
            var classDef2Offset = r.ReadUInt16();
            var class1Count     = r.ReadUInt16();
            var class2Count     = r.ReadUInt16();

            var coverage  = ParseCoverage(data[subBase..], coverageOffset);
            if (coverage.Length == 0) return;

            var classDef1 = ClassDef.Parse(data[subBase..], classDef1Offset);
            var classDef2 = ClassDef.Parse(data[subBase..], classDef2Offset);

            // Build the matrix: matrix[c1][c2] = xAdv1.
            var matrix = new short[class1Count][];
            for (var c1 = 0; c1 < class1Count; c1++)
            {
                matrix[c1] = new short[class2Count];
                for (var c2 = 0; c2 < class2Count; c2++)
                {
                    matrix[c1][c2] = ReadValueRecord(ref r, valueFormat1);
                    ReadValueRecord(ref r, valueFormat2);
                }
            }

            format2List.Add(new Format2Subtable(coverage, classDef1, classDef2, matrix, class2Count));
        }
        // Other posFormat values are ignored.
    }
}
