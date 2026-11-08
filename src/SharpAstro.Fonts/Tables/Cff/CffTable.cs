using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Tables.Cff;

/// <summary>
/// Parsed 'CFF ' (or 'CFF2') table. Holds the structural pieces needed to
/// interpret Type-2 charstrings into outline path commands.
///
/// <para>Thread-safe: all referenced INDEXes / DICTs are immutable once
/// constructed; charstring interpretation allocates per call.</para>
/// </summary>
internal sealed class CffTable
{
    public bool IsCff2 { get; }

    /// <summary>Top DICT (one per face; for CID fonts each FDArray entry has its own).</summary>
    public CffDict TopDict { get; }

    /// <summary>Global subroutines INDEX (shared across all glyphs).</summary>
    public CffIndex GlobalSubrs { get; }

    /// <summary>CharStrings INDEX — count == numGlyphs.</summary>
    public CffIndex CharStrings { get; }

    /// <summary>True if this is a CID-keyed CFF font.</summary>
    public bool IsCidKeyed { get; }

    /// <summary>FDSelect (CID only). Null for non-CID fonts.</summary>
    public CffFdSelect? FdSelect { get; }

    /// <summary>Per-FD private DICTs (CID) or single-element array (non-CID).</summary>
    public CffPrivateDict[] PrivateDicts { get; }

    /// <summary>Glyph count = CharStrings INDEX count. Authoritative for a bare CFF
    /// (no SFNT 'maxp' to cross-check against).</summary>
    public int NumGlyphs => CharStrings.Count;

    /// <summary>Units per em derived from the FontMatrix sx (1/sx, default 1000).</summary>
    public ushort UnitsPerEm { get; }

    /// <summary>FontBBox in font units [xMin yMin xMax yMax], or all-zero if absent.</summary>
    public short[] FontBBox { get; }

    /// <summary>GID→SID charset (GID→CID for CID-keyed fonts); null when a
    /// predefined charset (offset 0/1/2) is used.</summary>
    public CffCharset? Charset { get; }

    // CID→GID inverse of the charset, built once for CID-keyed fonts. Null for
    // non-CID fonts or a CID font with a predefined (offset ≤ 2) charset.
    private readonly Dictionary<uint, uint>? _cidToGid;

    // String INDEX — SIDs ≥ 391 name into it. Kept for glyph-name → GID lookup.
    private readonly CffIndex _strings;

    // Lazy name→GID reverse of the charset (non-CID only); built on first
    // GidForName call, published lock-free — a racing duplicate build is
    // idempotent and the loser's map is simply collected.
    private Dictionary<string, uint>? _nameToGid;

    // The built-in encoding (Top DICT op 16): where it is, in the program it was read from, and the code→GID
    // table built from it on first GidForCode call (published lock-free, like the name map).
    private readonly ReadOnlyMemory<byte> _cffData;
    private readonly int _encodingOffset;
    private uint[]? _codeToGid;

    private CffTable(bool isCff2, CffDict topDict, CffIndex globalSubrs,
        CffIndex charStrings, bool isCidKeyed, CffFdSelect? fdSelect,
        CffPrivateDict[] privateDicts, ushort unitsPerEm, short[] fontBBox,
        CffCharset? charset, CffIndex strings, ReadOnlyMemory<byte> cffData = default, int encodingOffset = 0)
    {
        _cffData = cffData;
        _encodingOffset = encodingOffset;
        IsCff2 = isCff2;
        TopDict = topDict;
        GlobalSubrs = globalSubrs;
        CharStrings = charStrings;
        IsCidKeyed = isCidKeyed;
        FdSelect = fdSelect;
        PrivateDicts = privateDicts;
        UnitsPerEm = unitsPerEm;
        FontBBox = fontBBox;
        Charset = charset;
        _strings = strings;
        _cidToGid = isCidKeyed && charset is not null ? charset.BuildCidToGid() : null;
    }

    /// <summary>
    /// Map a CID to its glyph id via the charset. Returns 0 (.notdef) for an
    /// unmapped CID. For a non-CID font, or a CID font with a predefined charset
    /// (no explicit GID→CID table), the CID is used as the GID directly when in
    /// range — the Identity case.
    /// </summary>
    public uint CidToGid(uint cid)
    {
        if (_cidToGid is not null)
            return _cidToGid.TryGetValue(cid, out var gid) ? gid : 0u;
        return cid < (uint)CharStrings.Count ? cid : 0u;
    }

    /// <summary>
    /// Glyph id of the glyph named <paramref name="name"/>, or 0. Name-keyed (non-CID)
    /// fonts only — a CID charset holds CIDs, not name SIDs. This is how a PDF simple
    /// font's <c>/Encoding</c> selects glyphs: char code → glyph name → charset. A bare
    /// subset CFF often carries no Encoding operator at all (Lithos-Bold in the Canon
    /// EOS450D manual), so the charset is the only name authority.
    /// </summary>
    public uint GidForName(string name)
    {
        if (IsCidKeyed) return 0u;
        var map = _nameToGid ?? BuildNameToGid();
        return map.TryGetValue(name, out var gid) ? gid : 0u;
    }

    private Dictionary<string, uint> BuildNameToGid()
    {
        var n = CharStrings.Count;
        var map = new Dictionary<string, uint>(n, StringComparer.Ordinal);
        for (var gid = 1; gid < n; gid++)
        {
            // No charset operator = the predefined ISOAdobe charset, which is SID == GID.
            var sid = Charset?.GetSid((uint)gid) ?? (ushort)gid;
            if (NameOfSid(sid) is not { } name) continue;
            map.TryAdd(name, (uint)gid); // first wins on (spec-illegal) duplicate names
        }
        System.Threading.Interlocked.CompareExchange(ref _nameToGid, map, null);
        return _nameToGid;
    }

    private string? NameOfSid(int sid)
    {
        if (sid < CffStandardStrings.Count) return CffStandardStrings.Get(sid);
        if (sid - CffStandardStrings.Count < _strings.Count)
            return System.Text.Encoding.Latin1.GetString(_strings.GetObject(sid - CffStandardStrings.Count));
        return null;
    }

    /// <summary>
    /// Glyph id the font's OWN encoding (Top DICT <c>Encoding</c>, CFF spec §12) assigns to the one-byte
    /// <paramref name="code"/>, or 0. This is what a PDF simple font with no <c>/Encoding</c> selects by: ISO 32000
    /// §9.6.5 sends a symbolic Type 1 font's codes through its built-in encoding, and a TeX math subset (MathDesign,
    /// CMSY) carries a custom one whose codes are not glyph indices. Taking the code as the index instead drew
    /// <c>bardbl</c> for every bullet and nothing for <c>(</c>, <c>=</c> or <c>∈</c>. With no Encoding operator the
    /// font's encoding is StandardEncoding; the Expert encoding is not mapped. Name-keyed fonts only.
    /// </summary>
    public uint GidForCode(uint code)
    {
        if (IsCidKeyed || code > 255) return 0u;
        var map = _codeToGid ?? BuildCodeToGid();
        return map[code];
    }

    private uint[] BuildCodeToGid()
    {
        var map = new uint[256];
        if (_encodingOffset == 0)
        {
            for (var code = 0; code < 256; code++)
            {
                var name = Outlines.AdobeStandardEncoding.NameOf(code);
                if (name != ".notdef") map[code] = GidForName(name);
            }
        }
        else if (_encodingOffset > 1 && _encodingOffset < _cffData.Length)
        {
            ParseCustomEncoding(_cffData.Span, _encodingOffset, map);
        }
        System.Threading.Interlocked.CompareExchange(ref _codeToGid, map, null);
        return _codeToGid;
    }

    // Format 0 lists a code per glyph from GID 1, format 1 runs of consecutive codes; the high bit of the format
    // adds supplements, further codes for glyphs named by SID. A table that runs off the end stops where it does.
    private void ParseCustomEncoding(ReadOnlySpan<byte> data, int offset, uint[] map)
    {
        var p = offset;
        if (p >= data.Length) return;
        var format = data[p++];
        var gid = 1u;
        var glyphs = (uint)CharStrings.Count;
        switch (format & 0x7F)
        {
            case 0:
            {
                if (p >= data.Length) return;
                var nCodes = data[p++];
                for (var i = 0; i < nCodes && p < data.Length; i++, gid++)
                {
                    var code = data[p++];
                    if (gid < glyphs) map[code] = gid;
                }
                break;
            }
            case 1:
            {
                if (p >= data.Length) return;
                var nRanges = data[p++];
                for (var r = 0; r < nRanges && p + 1 < data.Length; r++)
                {
                    int first = data[p++], nLeft = data[p++];
                    for (var code = first; code <= first + nLeft && code < 256; code++, gid++)
                        if (gid < glyphs) map[code] = gid;
                }
                break;
            }
            default:
                return;
        }
        if ((format & 0x80) == 0 || p >= data.Length) return;
        var nSups = data[p++];
        for (var s = 0; s < nSups && p + 2 < data.Length; s++)
        {
            var code = data[p];
            var sid = (data[p + 1] << 8) | data[p + 2];
            p += 3;
            if (NameOfSid(sid) is { } name && GidForName(name) is var g and not 0) map[code] = g;
        }
    }

    /// <summary>Pick the Private DICT applicable to <paramref name="gid"/>.</summary>
    public CffPrivateDict GetPrivateForGid(uint gid)
    {
        if (!IsCidKeyed) return PrivateDicts[0];
        var fd = FdSelect!.GetFdIndex(gid);
        return PrivateDicts[fd < PrivateDicts.Length ? fd : 0];
    }

    /// <summary>
    /// Parse 'CFF ' from <paramref name="cffData"/>. Works for a CFF embedded in an
    /// SFNT ('CFF ' table) and for a bare CFF program (CIDFontType0 /FontFile3).
    /// <paramref name="expectedNumGlyphs"/> is validated against the CharStrings
    /// count when ≥ 0 (the SFNT 'maxp' cross-check); pass -1 for a bare CFF, where
    /// the CharStrings count IS the glyph count.
    /// </summary>
    public static CffTable Parse(ReadOnlyMemory<byte> cffData, int expectedNumGlyphs = -1, bool isCff2 = false)
    {
        if (isCff2)
            throw new NotSupportedException("CFF2 not yet implemented (Phase 4 follow-up).");

        var span = cffData.Span;
        var r = new BigEndianReader(span);
        // CFF1 header: major(uint8) minor(uint8) hdrSize(uint8) offSize(uint8)
        var major = r.ReadByte();
        if (major != 1)
            throw new InvalidDataException($"CFF: unexpected major version {major}");
        var minor = r.ReadByte();
        var hdrSize = r.ReadByte();
        var offSize = r.ReadByte();
        _ = (minor, offSize);

        // Header may be larger than 4 in theory; skip any extra bytes.
        int pos = hdrSize;

        // Name INDEX (typically one entry).
        var nameIndex = CffIndex.Parse(cffData, pos);
        pos += nameIndex.TotalSize;

        // Top DICT INDEX (one entry per face — we take entry 0).
        var topDictIndex = CffIndex.Parse(cffData, pos);
        pos += topDictIndex.TotalSize;
        if (topDictIndex.Count == 0)
            throw new InvalidDataException("CFF: empty Top DICT INDEX");
        var topDict = CffDict.Parse(topDictIndex.GetObject(0));

        // String INDEX — kept: SIDs ≥ 391 name into it, and glyph-name → GID lookup
        // (PDF simple-font /Encoding) resolves names through it.
        var stringIndex = CffIndex.Parse(cffData, pos);
        pos += stringIndex.TotalSize;

        // Global Subrs INDEX.
        var globalSubrs = CffIndex.Parse(cffData, pos);
        // pos doesn't matter after this — remaining sections are referenced by absolute offsets in Top DICT.

        // CharStrings INDEX (absolute offset).
        if (!topDict.TryGetSingle(TopDictOps.CharStrings, out var csOff))
            throw new InvalidDataException("CFF Top DICT: missing CharStrings");
        var charStrings = CffIndex.Parse(cffData, (int)csOff);
        var numGlyphs = charStrings.Count;
        if (expectedNumGlyphs >= 0 && numGlyphs != expectedNumGlyphs)
            throw new InvalidDataException(
                $"CFF: CharStrings.Count ({numGlyphs}) != numGlyphs ({expectedNumGlyphs})");

        var isCid = topDict.Entries.ContainsKey(TopDictOps.Ros);
        CffFdSelect? fdSelect = null;
        CffPrivateDict[] privates;
        if (isCid)
        {
            // FDArray (Top DICT) → INDEX of Top-DICT-like dicts, each with its own Private DICT pointer.
            if (!topDict.TryGetSingle(TopDictOps.FdArray, out var fdArrayOff))
                throw new InvalidDataException("CID CFF: missing FDArray");
            if (!topDict.TryGetSingle(TopDictOps.FdSelect, out var fdSelectOff))
                throw new InvalidDataException("CID CFF: missing FDSelect");
            var fdArray = CffIndex.Parse(cffData, (int)fdArrayOff);
            fdSelect = CffFdSelect.Parse(span, (int)fdSelectOff, numGlyphs);
            privates = new CffPrivateDict[fdArray.Count];
            for (var i = 0; i < fdArray.Count; i++)
            {
                var fdDict = CffDict.Parse(fdArray.GetObject(i));
                privates[i] = ParsePrivate(cffData, fdDict);
            }
        }
        else
        {
            privates = [ParsePrivate(cffData, topDict)];
        }

        // Units per em from the FontMatrix (op 12 7): [sx shy shx sy tx ty]; upem = round(1/sx).
        // Default matrix is [0.001 0 0 0.001 0 0] → 1000 upem when the op is absent.
        var upem = (ushort)1000;
        if (topDict.TryGetArray(TopDictOps.FontMatrix, out var fm) && fm.Length >= 1 && fm[0] > 0)
            upem = (ushort)Math.Clamp(Math.Round(1.0 / fm[0]), 16, 16384);

        var bbox = new short[4];
        if (topDict.TryGetArray(TopDictOps.FontBbox, out var fb) && fb.Length >= 4)
            for (var i = 0; i < 4; i++)
                bbox[i] = (short)Math.Clamp(Math.Round(fb[i]), short.MinValue, short.MaxValue);

        // Charset (op 15). Offsets 0/1/2 are the predefined charsets (ISOAdobe/Expert/
        // ExpertSubset). A CID font always ships a custom charset at a real offset;
        // parse it so CID→GID selection is exact even under a renumbered subset. For
        // name-keyed fonts the charset is the name authority (GID → SID), consumed by
        // GidForName — under a predefined charset it stays null and SID == GID.
        CffCharset? charset = null;
        var charsetOff = (int)topDict.GetSingleOr(TopDictOps.Charset, 0);
        if (charsetOff > 2)
            charset = CffCharset.Parse(span, charsetOff, numGlyphs);

        // Encoding (op 16): 0 = StandardEncoding (also the default), 1 = Expert, else the offset of a custom one.
        // A CID-keyed font has none.
        var encodingOff = isCid ? 0 : (int)topDict.GetSingleOr(TopDictOps.Encoding, 0);

        return new CffTable(isCff2: false, topDict, globalSubrs, charStrings,
            isCidKeyed: isCid, fdSelect, privates, upem, bbox, charset, stringIndex, cffData, encodingOff);
    }

    private static CffPrivateDict ParsePrivate(ReadOnlyMemory<byte> cff, CffDict parentDict)
    {
        if (!parentDict.TryGetArray(TopDictOps.Private, out var pv) || pv.Length < 2)
            return new CffPrivateDict(CffDict.Parse([]), CffIndex.Empty, 0, 0);

        var pSize = (int)pv[0];
        var pOff = (int)pv[1];
        var privDict = CffDict.Parse(cff.Span.Slice(pOff, pSize));

        var subrs = CffIndex.Empty;
        if (privDict.TryGetSingle(PrivateDictOps.Subrs, out var subrOff))
        {
            // Subrs offset is relative to the Private DICT start.
            subrs = CffIndex.Parse(cff, pOff + (int)subrOff);
        }

        var defaultWidth = privDict.GetSingleOr(PrivateDictOps.DefaultWidthX, 0);
        var nominalWidth = privDict.GetSingleOr(PrivateDictOps.NominalWidthX, 0);
        return new CffPrivateDict(privDict, subrs, defaultWidth, nominalWidth);
    }

    /// <summary>
    /// Interpret the charstring for <paramref name="gid"/>, emitting outline
    /// commands to <paramref name="sink"/>.
    /// </summary>
    public void DrawGlyph(uint gid, IGlyphSink sink) => DrawGlyph(gid, sink, composite: true);

    // A composite's base and accent are drawn as plain glyphs: the spec forbids nesting composites, and refusing
    // to keeps a malformed font (a composite naming itself) from recursing without end.
    private void DrawGlyph(uint gid, IGlyphSink sink, bool composite)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(gid, (uint)CharStrings.Count);
        var cs = CharStrings.GetObjectMemory((int)gid);
        var priv = GetPrivateForGid(gid);
        Type2CharstringInterpreter.Execute(cs, GlobalSubrs, priv.LocalSubrs, sink,
            composite ? (adx, ady, baseCode, accentCode) => DrawComposite(sink, adx, ady, baseCode, accentCode) : null);
    }

    // The base and accent are named by StandardEncoding codes, whatever the font's encoding, and found by name in
    // the charset; a CID-keyed font has no names and so no composites.
    private void DrawComposite(IGlyphSink sink, float adx, float ady, int baseCode, int accentCode)
    {
        var baseGid = GidForName(AdobeStandardEncoding.NameOf(baseCode));
        var accentGid = GidForName(AdobeStandardEncoding.NameOf(accentCode));
        if (baseGid != 0) DrawGlyph(baseGid, sink, composite: false);
        if (accentGid != 0) DrawGlyph(accentGid, new TranslatingGlyphSink(sink, adx, ady), composite: false);
    }
}

/// <summary>
/// Parsed Private DICT for a single face / FD entry.
/// </summary>
internal sealed class CffPrivateDict
{
    public CffDict Dict { get; }
    public CffIndex LocalSubrs { get; }
    public double DefaultWidthX { get; }
    public double NominalWidthX { get; }

    public CffPrivateDict(CffDict dict, CffIndex localSubrs,
        double defaultWidth, double nominalWidth)
    {
        Dict = dict;
        LocalSubrs = localSubrs;
        DefaultWidthX = defaultWidth;
        NominalWidthX = nominalWidth;
    }
}
