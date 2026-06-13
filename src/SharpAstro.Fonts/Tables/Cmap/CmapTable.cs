using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cmap;

/// <summary>
/// Parsed 'cmap' table — character-to-glyph index map.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/cmap
/// </summary>
public sealed class CmapTable
{
    public IReadOnlyList<CmapSubtable> Subtables { get; }

    private CmapTable(IReadOnlyList<CmapSubtable> subtables)
    {
        Subtables = subtables;
    }

    /// <summary>
    /// Pick the "best" Unicode subtable using the platform/encoding precedence
    /// recommended by Microsoft's OpenType spec:
    ///
    ///   (3, 10) Windows Unicode full repertoire (UCS-4) — format 12/13
    ///   (0, 6)  Unicode full repertoire — format 13
    ///   (0, 4)  Unicode 2.0+ full repertoire
    ///   (3, 1)  Windows Unicode BMP — format 4/6
    ///   (0, 3)  Unicode 2.0 BMP
    ///
    /// Returns null if no Unicode subtable is found.
    /// </summary>
    public CmapSubtable? PreferredUnicodeSubtable()
        // Falls back to the first subtable when there is no genuine Unicode one, for callers that
        // just want *a* subtable. WARNING: that fallback is a char-code-keyed (1,0)/(3,0) cmap, so
        // looking a *codepoint* up in it returns the wrong glyph (e.g. '×' U+00D7=215 hits the glyph
        // at char-code 215). Codepoint-based callers must use <see cref="GenuineUnicodeSubtable"/>.
        => GenuineUnicodeSubtable() ?? (Subtables.Count > 0 ? Subtables[0] : null);

    /// <summary>The best *genuine* Unicode subtable (a real Unicode platform/encoding), or null if the
    /// font has none — never the char-code-keyed fallback, so a codepoint lookup against the result is
    /// always meaningful.</summary>
    public CmapSubtable? GenuineUnicodeSubtable()
    {
        // Order by descending preference
        ReadOnlySpan<(ushort plat, ushort enc)> order =
        [
            (3, 10), (0, 6), (0, 4), (3, 1), (0, 3), (0, 2), (0, 1), (0, 0),
        ];
        foreach (var (plat, enc) in order)
            foreach (var s in Subtables)
                if (s.PlatformId == plat && s.EncodingId == enc)
                    return s;
        return null;
    }

    /// <summary>
    /// Look up a glyph for a (base codepoint, variation selector) pair via the
    /// cmap format 14 subtable. If the format 14 subtable says "use default",
    /// falls back to the preferred Unicode subtable. Returns 0 if not mapped.
    /// </summary>
    public uint GetVariationGlyphId(uint codepoint, uint variationSelector)
    {
        foreach (var s in Subtables)
        {
            if (s is Format14Subtable f14)
            {
                var result = f14.GetVariationGlyphId(codepoint, variationSelector, out var gid);
                return result switch
                {
                    Format14Subtable.VariationResult.Found => gid,
                    Format14Subtable.VariationResult.UseDefault =>
                        PreferredUnicodeSubtable()?.GetGlyphId(codepoint) ?? 0u,
                    _ => 0u, // NotDefined
                };
            }
        }
        return 0u;
    }

    /// <summary>Find a subtable by platform / encoding id, or null.</summary>
    public CmapSubtable? Find(ushort platformId, ushort encodingId)
    {
        foreach (var s in Subtables)
            if (s.PlatformId == platformId && s.EncodingId == encodingId)
                return s;
        return null;
    }

    /// <summary>
    /// Look up a glyph id using the strategy described by
    /// <paramref name="hint"/>. <paramref name="codepoint"/> is the natural
    /// Unicode codepoint, <paramref name="charCode"/> is the PDF byte/CID
    /// (often equal to <paramref name="codepoint"/> for plain text).
    /// Returns 0 if no strategy yields a glyph.
    /// </summary>
    public uint GetGlyphIdHinted(uint codepoint, uint charCode, GlyphMapHint hint, ushort numGlyphs)
    {
        // A cmap subtable can return a glyph id past the font's glyph count — broken
        // PDF subsets do this routinely (their cmap is built for the full glyph layout
        // but the embedded glyf/maxp were subsetted to fewer glyphs, so the upper
        // codepoints map off the end). Such an id has no outline and must NOT be
        // returned: treat it as a miss so the next strategy (e.g. the direct-GID
        // fallback) gets a chance. Without this, an out-of-range cmap hit short-circuits
        // the fallback and the glyph renders as .notdef.
        bool InRange(uint gid) => gid != 0 && gid < numGlyphs;

        switch (hint)
        {
            case GlyphMapHint.CharCodeIsGID:
                return charCode > 0 && charCode < numGlyphs ? charCode : 0u;

            case GlyphMapHint.EmbeddedSubset:
            {
                // Genuine Unicode subtable ONLY. The PreferredUnicodeSubtable fallback is a
                // char-code-keyed (1,0)/(3,0) cmap; looking a codepoint up there returns the wrong
                // glyph when the codepoint collides with a char-code (e.g. '×' U+00D7 → char-code
                // 0xD7's glyph). These subset fonts have no real Unicode cmap, so we drop straight to
                // the char-code paths below, which map the PDF code through the embedded cmap.
                var unicode = GenuineUnicodeSubtable();
                var gid = unicode?.GetGlyphId(codepoint) ?? 0u;
                if (InRange(gid)) return gid;
                if (charCode > 0)
                {
                    var symbol = Find(3, 0); // (Windows, Symbol)
                    if (symbol is not null)
                    {
                        // MS Symbol cmap, conventional PUA offset (U+F000+code) — e.g. Revit's
                        // XXTIIT+Arial subset.
                        gid = symbol.GetGlyphId(0xF000 + charCode);
                        if (InRange(gid)) return gid;
                        // …and the RAW code: mPDF (and other CJK subsetters) write a (3,0) subtable
                        // keyed by the raw 1-byte code, NOT PUA-offset, and CID != GID for these
                        // subsets. Without this the glyph falls through to the wrong direct-GID below
                        // (the garbled-CJK bug). Mac (1,0) stays skipped — it maps charCodes to wrong
                        // GIDs in other subsets (Tahoma/ISOCPEUR); (3,0)-raw covers the CJK case.
                        gid = symbol.GetGlyphId(charCode);
                        if (InRange(gid)) return gid;
                    }
                    // Direct GID fallback — Identity-style mapping in the subset.
                    if (charCode < numGlyphs) return charCode;
                }
                return 0u;
            }

            case GlyphMapHint.Unicode:
            {
                var unicode = PreferredUnicodeSubtable();
                var gid = unicode?.GetGlyphId(codepoint) ?? 0u;
                if (InRange(gid)) return gid;
                if (charCode > 0 && unicode is not null)
                    gid = unicode.GetGlyphId(charCode);
                return InRange(gid) ? gid : 0u;
            }

            case GlyphMapHint.Auto:
            default:
            {
                var unicode = PreferredUnicodeSubtable();
                var gid = unicode?.GetGlyphId(codepoint) ?? 0u;
                if (InRange(gid)) return gid;
                if (charCode == 0) return 0u;
                var symbol = Find(3, 0);
                if (symbol is not null)
                {
                    gid = symbol.GetGlyphId(0xF000 + charCode);
                    if (InRange(gid)) return gid;
                }
                var macRoman = Find(1, 0); // (Mac, Roman)
                if (macRoman is not null)
                {
                    gid = macRoman.GetGlyphId(charCode);
                    if (InRange(gid)) return gid;
                }
                if (unicode is not null)
                {
                    gid = unicode.GetGlyphId(charCode);
                    if (InRange(gid)) return gid;
                }
                if (charCode < numGlyphs) return charCode;
                return 0u;
            }
        }
    }

    public static CmapTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt16();
        if (version != 0)
            throw new InvalidDataException($"cmap: unsupported version {version}");
        var numTables = r.ReadUInt16();

        // Encoding records: platformID (uint16), encodingID (uint16), subtableOffset (uint32)
        // Parse all records first so we can decode subtables in a second pass.
        var records = new (ushort plat, ushort enc, uint off)[numTables];
        for (var i = 0; i < numTables; i++)
        {
            var plat = r.ReadUInt16();
            var enc = r.ReadUInt16();
            var off = r.ReadUInt32();
            records[i] = (plat, enc, off);
        }

        var subtables = new List<CmapSubtable>(numTables);
        foreach (var (plat, enc, off) in records)
        {
            var sub = CmapSubtable.TryParse(data, (int)off, plat, enc);
            if (sub is not null)
                subtables.Add(sub);
        }
        return new CmapTable(subtables);
    }
}
