using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// One lookup from a GSUB/GPOS LookupList: its (post-Extension) type, flags, and
/// raw subtable slices. Each subtable slice runs from the subtable's start to the
/// END of the layout table — subtables reference coverage/class data by forward
/// offsets beyond their own header, so a length-bounded slice would truncate them.
/// Subtable content is interpreted per lookup type by the appliers (H1+).
/// </summary>
internal sealed class Lookup
{
    public required ushort Type { get; init; }
    public required LookupFlags Flags { get; init; }
    /// <summary>GDEF MarkGlyphSets index; only meaningful when <see cref="LookupFlags.UseMarkFilteringSet"/> is set.</summary>
    public required ushort MarkFilteringSet { get; init; }
    public required ReadOnlyMemory<byte>[] Subtables { get; init; }

    /// <summary>
    /// A digest of the glyphs this lookup can act on at the position where it is invoked — the
    /// union of every subtable's entry coverage (the coverage the applier probes against the
    /// current glyph before doing anything). Built once per font at parse time. The runner uses
    /// it to skip a glyph whose id can't be in any subtable's coverage, avoiding the coverage
    /// binary search and GDEF class lookup for the many non-matching positions. Conservatively
    /// saturated (matches everything) when a subtable's coverage can't be located, so the skip is
    /// always safe. See <see cref="SetDigest"/>.
    /// </summary>
    public required SetDigest Digest { get; init; }
}

/// <summary>
/// The structure GSUB and GPOS share — ScriptList (script → LangSys → feature
/// indices), FeatureList (feature tag → lookup indices), LookupList (typed,
/// flagged subtables) — parsed once per font. GSUB Extension (type 7) and GPOS
/// Extension (type 9) lookups are unwrapped at parse time: the lookup's type
/// becomes the wrapped type and the subtable slices point at the wrapped data,
/// so appliers never see the indirection. (The core's kerning-only GPOS slice
/// skips Extension entirely — fonts wrapping PairPos that way silently lose
/// kerning there; this parser is where the shaped path gets it right.)
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/chapter2</para>
/// </summary>
internal sealed class LayoutTable
{
    /// <summary>(scriptTag → (langSysTag → feature index list)); the default LangSys is under <see cref="DefaultLangSysTag"/>.</summary>
    private readonly Dictionary<Tag, Dictionary<Tag, LangSys>> _scripts;
    private readonly (Tag Tag, ushort[] LookupIndices)[] _features;
    public Lookup[] Lookups { get; }

    /// <summary>Synthetic key for a script's default LangSys ('dflt' is not a registered langsys tag).</summary>
    private static readonly Tag DefaultLangSysTag = new("dflt");
    public static readonly Tag DfltScript = new("DFLT");

    private readonly record struct LangSys(ushort RequiredFeatureIndex, ushort[] FeatureIndices)
    {
        public const ushort NoRequiredFeature = 0xFFFF;
    }

    private LayoutTable(Dictionary<Tag, Dictionary<Tag, LangSys>> scripts,
        (Tag, ushort[])[] features, Lookup[] lookups)
    {
        _scripts = scripts;
        _features = features;
        Lookups = lookups;
    }

    /// <summary>Feature tags present in the FeatureList (diagnostics/tests).</summary>
    public IEnumerable<Tag> FeatureTags => _features.Select(f => f.Tag).Distinct();

    /// <summary>Script tags present in the ScriptList (diagnostics/tests).</summary>
    public IEnumerable<Tag> ScriptTags => _scripts.Keys;

    /// <summary>
    /// Resolve the feature set for (<paramref name="script"/>, default language system):
    /// the script's default LangSys, falling back to 'DFLT' when the script is absent.
    /// Calls <paramref name="collect"/> once per (featureTag, lookupIndices) the LangSys
    /// references, plus the required feature (if any) under <paramref name="requiredTag"/>.
    /// Returns false when neither the script nor DFLT exists.
    /// </summary>
    public bool TryCollectFeatures(Tag script, Tag requiredTag,
        Action<Tag, ushort[]> collect)
    {
        if (!_scripts.TryGetValue(script, out var langSystems)
            && !_scripts.TryGetValue(DfltScript, out langSystems))
            return false;
        if (!langSystems.TryGetValue(DefaultLangSysTag, out var langSys))
            return false;

        if (langSys.RequiredFeatureIndex != LangSys.NoRequiredFeature
            && langSys.RequiredFeatureIndex < _features.Length)
        {
            collect(requiredTag, _features[langSys.RequiredFeatureIndex].LookupIndices);
        }

        foreach (var fi in langSys.FeatureIndices)
        {
            if (fi < _features.Length)
                collect(_features[fi].Tag, _features[fi].LookupIndices);
        }
        return true;
    }

    /// <summary>
    /// Parse a GSUB or GPOS table. <paramref name="extensionLookupType"/> is the
    /// table's Extension lookup type (GSUB: 7, GPOS: 9); <paramref name="maxLookupType"/>
    /// bounds valid types (GSUB: 8, GPOS: 9). Returns null on malformed/unsupported
    /// headers — a bad layout table degrades to unshaped output, never a throw.
    /// </summary>
    public static LayoutTable? Parse(ReadOnlyMemory<byte> table,
        ushort extensionLookupType, ushort maxLookupType)
    {
        var data = table.Span;
        if (data.Length < 10) return null;
        var r = new BigEndianReader(data);
        var major = r.ReadUInt16();
        var minor = r.ReadUInt16();
        if (major != 1) return null;

        var scriptListOffset = r.ReadUInt16();
        var featureListOffset = r.ReadUInt16();
        var lookupListOffset = r.ReadUInt16();
        // Version 1.1 adds featureVariationsOffset (variable-font feature swaps) — not consumed.
        _ = minor;

        // GSUB's Extension type is 7, GPOS's is 9; that also distinguishes the tables, which the
        // digest builder needs because the context/chained types (5/6 in GSUB, 7/8 in GPOS) locate
        // their entry coverage differently than the offset-2 types they otherwise share.
        var isSubstitution = extensionLookupType == 7;

        var scripts = ParseScriptList(data, scriptListOffset);
        var features = ParseFeatureList(data, featureListOffset);
        var lookups = ParseLookupList(table, lookupListOffset, extensionLookupType, maxLookupType, isSubstitution);
        if (scripts is null || features is null || lookups is null) return null;

        return new LayoutTable(scripts, features, lookups);
    }

    private static Dictionary<Tag, Dictionary<Tag, LangSys>>? ParseScriptList(
        ReadOnlySpan<byte> data, int scriptListOffset)
    {
        if (scriptListOffset <= 0 || scriptListOffset + 2 > data.Length) return null;
        var scriptList = data[scriptListOffset..];
        var r = new BigEndianReader(scriptList);
        var scriptCount = r.ReadUInt16();
        if (r.Remaining < scriptCount * 6) return null;

        var scripts = new Dictionary<Tag, Dictionary<Tag, LangSys>>(scriptCount);
        for (var i = 0; i < scriptCount; i++)
        {
            var tag = r.ReadTag();
            var scriptOffset = r.ReadUInt16();
            if (scriptOffset == 0 || scriptOffset + 4 > scriptList.Length) continue;

            var script = scriptList[scriptOffset..];
            var sr = new BigEndianReader(script);
            var defaultLangSysOffset = sr.ReadUInt16();
            var langSysCount = sr.ReadUInt16();

            var langSystems = new Dictionary<Tag, LangSys>(langSysCount + 1);
            if (defaultLangSysOffset > 0 && TryParseLangSys(script, defaultLangSysOffset, out var dflt))
                langSystems[DefaultLangSysTag] = dflt;

            for (var l = 0; l < langSysCount && sr.Remaining >= 6; l++)
            {
                var lsTag = sr.ReadTag();
                var lsOffset = sr.ReadUInt16();
                if (TryParseLangSys(script, lsOffset, out var ls))
                    langSystems[lsTag] = ls;
            }

            scripts[tag] = langSystems;
        }
        return scripts;
    }

    private static bool TryParseLangSys(ReadOnlySpan<byte> script, int offset, out LangSys langSys)
    {
        langSys = default;
        if (offset <= 0 || offset + 6 > script.Length) return false;
        var r = new BigEndianReader(script[offset..]);
        r.Skip(2); // lookupOrderOffset — reserved, always 0
        var requiredFeatureIndex = r.ReadUInt16();
        var featureIndexCount = r.ReadUInt16();
        if (r.Remaining < featureIndexCount * 2) return false;
        var indices = new ushort[featureIndexCount];
        for (var i = 0; i < featureIndexCount; i++) indices[i] = r.ReadUInt16();
        langSys = new LangSys(requiredFeatureIndex, indices);
        return true;
    }

    private static (Tag, ushort[])[]? ParseFeatureList(ReadOnlySpan<byte> data, int featureListOffset)
    {
        if (featureListOffset <= 0 || featureListOffset + 2 > data.Length) return null;
        var featureList = data[featureListOffset..];
        var r = new BigEndianReader(featureList);
        var featureCount = r.ReadUInt16();
        if (r.Remaining < featureCount * 6) return null;

        var features = new (Tag, ushort[])[featureCount];
        for (var i = 0; i < featureCount; i++)
        {
            var tag = r.ReadTag();
            var featureOffset = r.ReadUInt16();
            var lookups = Array.Empty<ushort>();
            if (featureOffset > 0 && featureOffset + 4 <= featureList.Length)
            {
                var fr = new BigEndianReader(featureList[featureOffset..]);
                fr.Skip(2); // featureParamsOffset — only meaningful for ss01+/cv01+/size
                var lookupCount = fr.ReadUInt16();
                if (fr.Remaining >= lookupCount * 2)
                {
                    lookups = new ushort[lookupCount];
                    for (var l = 0; l < lookupCount; l++) lookups[l] = fr.ReadUInt16();
                }
            }
            features[i] = (tag, lookups);
        }
        return features;
    }

    private static Lookup[]? ParseLookupList(ReadOnlyMemory<byte> table, int lookupListOffset,
        ushort extensionLookupType, ushort maxLookupType, bool isSubstitution)
    {
        var data = table.Span;
        if (lookupListOffset <= 0 || lookupListOffset + 2 > data.Length) return null;
        var r = new BigEndianReader(data[lookupListOffset..]);
        var lookupCount = r.ReadUInt16();
        if (r.Remaining < lookupCount * 2) return null;

        var lookupOffsets = new ushort[lookupCount];
        for (var i = 0; i < lookupCount; i++) lookupOffsets[i] = r.ReadUInt16();

        var lookups = new Lookup[lookupCount];
        for (var i = 0; i < lookupCount; i++)
        {
            lookups[i] = ParseLookup(table, lookupListOffset + lookupOffsets[i],
                extensionLookupType, maxLookupType, isSubstitution);
        }
        return lookups;
    }

    private static readonly Lookup EmptyLookup = new()
    {
        Type = 0,
        Flags = LookupFlags.None,
        MarkFilteringSet = 0,
        Subtables = [],
        Digest = default, // no subtables → never consulted (the runner skips empty lookups)
    };

    private static Lookup ParseLookup(ReadOnlyMemory<byte> table, int lookupBase,
        ushort extensionLookupType, ushort maxLookupType, bool isSubstitution)
    {
        var data = table.Span;
        if (lookupBase + 6 > data.Length) return EmptyLookup;
        var r = new BigEndianReader(data[lookupBase..]);
        var type = r.ReadUInt16();
        var flags = (LookupFlags)r.ReadUInt16();
        var subTableCount = r.ReadUInt16();
        if (r.Remaining < subTableCount * 2) return EmptyLookup;

        var subOffsets = new int[subTableCount];
        for (var s = 0; s < subTableCount; s++) subOffsets[s] = r.ReadUInt16();

        ushort markFilteringSet = 0;
        if ((flags & LookupFlags.UseMarkFilteringSet) != 0 && r.Remaining >= 2)
            markFilteringSet = r.ReadUInt16();

        // Collect subtable slices, unwrapping Extension indirection. Each slice runs to the
        // end of the layout table so forward offsets inside the subtable stay in range.
        var subtables = new List<ReadOnlyMemory<byte>>(subTableCount);
        var resolvedType = type;
        foreach (var subOffset in subOffsets)
        {
            var subStart = lookupBase + subOffset;
            if (subStart <= lookupBase || subStart >= data.Length) continue;

            if (type == extensionLookupType)
            {
                // ExtensionSubst/ExtensionPos format 1: u16 format, u16 extensionLookupType, u32 extensionOffset
                if (subStart + 8 > data.Length) continue;
                var er = new BigEndianReader(data[subStart..]);
                var extFormat = er.ReadUInt16();
                var wrappedType = er.ReadUInt16();
                var extensionOffset = (int)er.ReadUInt32();
                if (extFormat != 1 || wrappedType == 0 || wrappedType == extensionLookupType
                    || wrappedType > maxLookupType)
                    continue;
                // Spec: all subtables of one Extension lookup must wrap the same type.
                // A mismatching straggler is dropped rather than corrupting dispatch.
                if (resolvedType != extensionLookupType && wrappedType != resolvedType) continue;
                resolvedType = wrappedType;
                var wrappedStart = subStart + extensionOffset;
                if (wrappedStart <= subStart || wrappedStart >= data.Length) continue;
                subtables.Add(table[wrappedStart..]);
            }
            else
            {
                subtables.Add(table[subStart..]);
            }
        }

        // An Extension lookup whose every subtable was invalid dispatches as type 0 (= never applied).
        if (type == extensionLookupType && resolvedType == extensionLookupType)
            resolvedType = 0;

        var subtableArr = subtables.ToArray();
        return new Lookup
        {
            Type = resolvedType,
            Flags = flags,
            MarkFilteringSet = markFilteringSet,
            Subtables = subtableArr,
            Digest = BuildDigest(resolvedType, isSubstitution, subtableArr),
        };
    }

    /// <summary>
    /// Build the <see cref="Lookup.Digest"/>: the union of each subtable's entry coverage (the
    /// coverage the applier probes against the current glyph before it does anything). If any
    /// subtable's coverage can't be located or enumerated, the digest is saturated to match every
    /// glyph — the lookup is then never wrongly skipped, it just forgoes the optimization.
    /// </summary>
    private static SetDigest BuildDigest(ushort type, bool isSubstitution, ReadOnlyMemory<byte>[] subtables)
    {
        var digest = default(SetDigest);
        foreach (var subtable in subtables)
        {
            var span = subtable.Span;
            var coverageOffset = EntryCoverageOffset(type, isSubstitution, span);
            if (coverageOffset < 0 || !Coverage.AddToDigest(span, coverageOffset, ref digest))
            {
                digest.SaturateAll();
                return digest;
            }
        }
        return digest;
    }

    /// <summary>
    /// The offset, within <paramref name="subtable"/>, of the coverage table that gates whether the
    /// applier acts at the current glyph — the "entry" coverage. For every GSUB type (1/2/3/4, and 8
    /// reverse-chaining) and every GPOS type (1/2/3, and the mark types 4/5/6 whose entry is the
    /// <em>mark</em> coverage) that coverage sits at offset 2. Only the contextual types differ:
    /// context (GSUB 5 / GPOS 7) and chained context (GSUB 6 / GPOS 8) formats 1 and 2 also use
    /// offset 2, but format 3 inlines its coverage array — the entry is the first input coverage.
    /// Returns −1 when the subtable is too short to classify (caller saturates the digest).
    /// </summary>
    private static int EntryCoverageOffset(ushort type, bool isSubstitution, ReadOnlySpan<byte> subtable)
    {
        if (subtable.Length < 2) return -1;
        var isContext = isSubstitution ? type == 5 : type == 7;
        var isChained = isSubstitution ? type == 6 : type == 8;
        if (!isContext && !isChained)
            return subtable.Length >= 4 ? ReadU16Span(subtable, 2) : -1;

        var format = ReadU16Span(subtable, 0);
        if (format is 1 or 2)
            return subtable.Length >= 4 ? ReadU16Span(subtable, 2) : -1;
        if (format != 3) return -1;

        if (isContext)
            // SequenceContextFormat3: format(2), glyphCount(2), seqLookupCount(2), coverageOffsets[].
            return subtable.Length >= 8 ? ReadU16Span(subtable, 6) : -1;

        // ChainedSequenceContextFormat3: format(2), backtrackGlyphCount(2), backtrackCoverage[],
        // inputGlyphCount(2), inputCoverage[]… — the entry is inputCoverage[0].
        if (subtable.Length < 4) return -1;
        var backtrackCount = ReadU16Span(subtable, 2);
        var inputCovPos = 6 + backtrackCount * 2; // skip backtrack coverages + inputGlyphCount
        return inputCovPos + 2 <= subtable.Length ? ReadU16Span(subtable, inputCovPos) : -1;
    }

    private static ushort ReadU16Span(ReadOnlySpan<byte> b, int offset)
        => (ushort)((b[offset] << 8) | b[offset + 1]);
}
