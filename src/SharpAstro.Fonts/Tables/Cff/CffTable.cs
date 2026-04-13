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

    private CffTable(bool isCff2, CffDict topDict, CffIndex globalSubrs,
        CffIndex charStrings, bool isCidKeyed, CffFdSelect? fdSelect,
        CffPrivateDict[] privateDicts)
    {
        IsCff2 = isCff2;
        TopDict = topDict;
        GlobalSubrs = globalSubrs;
        CharStrings = charStrings;
        IsCidKeyed = isCidKeyed;
        FdSelect = fdSelect;
        PrivateDicts = privateDicts;
    }

    /// <summary>Pick the Private DICT applicable to <paramref name="gid"/>.</summary>
    public CffPrivateDict GetPrivateForGid(uint gid)
    {
        if (!IsCidKeyed) return PrivateDicts[0];
        var fd = FdSelect!.GetFdIndex(gid);
        return PrivateDicts[fd < PrivateDicts.Length ? fd : 0];
    }

    /// <summary>
    /// Parse 'CFF ' from <paramref name="cffData"/>. Caller is responsible
    /// for choosing the CFF1 vs CFF2 path via <paramref name="isCff2"/>
    /// (currently only CFF1 supported).
    /// </summary>
    public static CffTable Parse(ReadOnlyMemory<byte> cffData, int numGlyphs, bool isCff2 = false)
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
        if (charStrings.Count != numGlyphs)
            throw new InvalidDataException(
                $"CFF: CharStrings.Count ({charStrings.Count}) != numGlyphs ({numGlyphs})");

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

        return new CffTable(isCff2: false, topDict, globalSubrs, charStrings,
            isCidKeyed: isCid, fdSelect, privates);
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
