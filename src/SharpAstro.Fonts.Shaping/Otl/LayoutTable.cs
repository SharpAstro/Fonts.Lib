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

        var scripts = ParseScriptList(data, scriptListOffset);
        var features = ParseFeatureList(data, featureListOffset);
        var lookups = ParseLookupList(table, lookupListOffset, extensionLookupType, maxLookupType);
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
        ushort extensionLookupType, ushort maxLookupType)
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
                extensionLookupType, maxLookupType);
        }
        return lookups;
    }

    private static readonly Lookup EmptyLookup = new()
    {
        Type = 0,
        Flags = LookupFlags.None,
        MarkFilteringSet = 0,
        Subtables = [],
    };

    private static Lookup ParseLookup(ReadOnlyMemory<byte> table, int lookupBase,
        ushort extensionLookupType, ushort maxLookupType)
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

        return new Lookup
        {
            Type = resolvedType,
            Flags = flags,
            MarkFilteringSet = markFilteringSet,
            Subtables = subtables.ToArray(),
        };
    }
}
