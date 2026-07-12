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

    /// <summary>GID→CID charset (CID fonts only); null otherwise or when a
    /// predefined charset (offset 0/1/2) is used.</summary>
    public CffCharset? Charset { get; }

    // CID→GID inverse of the charset, built once for CID-keyed fonts. Null for
    // non-CID fonts or a CID font with a predefined (offset ≤ 2) charset.
    private readonly Dictionary<uint, uint>? _cidToGid;

    private CffTable(bool isCff2, CffDict topDict, CffIndex globalSubrs,
        CffIndex charStrings, bool isCidKeyed, CffFdSelect? fdSelect,
        CffPrivateDict[] privateDicts, ushort unitsPerEm, short[] fontBBox,
        CffCharset? charset)
    {
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

        // String INDEX (we don't need string lookup yet — skip it).
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
        // ExpertSubset) — only meaningful for non-CID fonts, where we don't need the
        // GID→CID map anyway. A CID font always ships a custom charset at a real offset;
        // parse it so CID→GID selection is exact even under a renumbered subset.
        CffCharset? charset = null;
        var charsetOff = (int)topDict.GetSingleOr(TopDictOps.Charset, 0);
        if (isCid && charsetOff > 2)
            charset = CffCharset.Parse(span, charsetOff, numGlyphs);

        return new CffTable(isCff2: false, topDict, globalSubrs, charStrings,
            isCidKeyed: isCid, fdSelect, privates, upem, bbox, charset);
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
    public void DrawGlyph(uint gid, IGlyphSink sink)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(gid, (uint)CharStrings.Count);
        var cs = CharStrings.GetObjectMemory((int)gid);
        var priv = GetPrivateForGid(gid);
        Type2CharstringInterpreter.Execute(cs, GlobalSubrs, priv.LocalSubrs, sink);
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
