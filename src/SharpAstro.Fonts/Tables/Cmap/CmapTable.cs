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
        return Subtables.Count > 0 ? Subtables[0] : null;
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
        switch (hint)
        {
            case GlyphMapHint.CharCodeIsGID:
                return charCode > 0 && charCode < numGlyphs ? charCode : 0u;

            case GlyphMapHint.EmbeddedSubset:
            {
                var unicode = PreferredUnicodeSubtable();
                var gid = unicode?.GetGlyphId(codepoint) ?? 0u;
                if (gid != 0) return gid;
                if (charCode > 0)
                {
                    // MS Symbol cmap with PUA offset.
                    var symbol = Find(3, 0); // (Windows, Symbol)
                    if (symbol is not null)
                    {
                        gid = symbol.GetGlyphId(0xF000 + charCode);
                        if (gid != 0) return gid;
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
                if (gid != 0) return gid;
                if (charCode > 0 && unicode is not null)
                    gid = unicode.GetGlyphId(charCode);
                return gid;
            }

            case GlyphMapHint.Auto:
            default:
            {
                var unicode = PreferredUnicodeSubtable();
                var gid = unicode?.GetGlyphId(codepoint) ?? 0u;
                if (gid != 0) return gid;
                if (charCode == 0) return 0u;
                var symbol = Find(3, 0);
                if (symbol is not null)
                {
                    gid = symbol.GetGlyphId(0xF000 + charCode);
                    if (gid != 0) return gid;
                }
                var macRoman = Find(1, 0); // (Mac, Roman)
                if (macRoman is not null)
                {
                    gid = macRoman.GetGlyphId(charCode);
                    if (gid != 0) return gid;
                }
                if (unicode is not null)
                {
                    gid = unicode.GetGlyphId(charCode);
                    if (gid != 0) return gid;
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
