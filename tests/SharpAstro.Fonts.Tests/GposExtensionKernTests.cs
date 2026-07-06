using System.Buffers.Binary;
using SharpAstro.Fonts.Tables.Gpos;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Regression for the core GPOS kerning slice's Extension (LookupType 9) unwrapping.
/// Some fonts put all their PairPos kerning behind Extension lookups; before the fix,
/// <see cref="GposTable"/> skipped anything that wasn't a direct type-2 lookup, so
/// <c>GetKerning</c> silently returned 0 for those fonts. These tests hand-assemble a
/// minimal GPOS table (no test font ships an Extension-wrapped pair here) and check the
/// kern is found through the wrapper — and that a direct type-2 lookup still works.
/// </summary>
public class GposExtensionKernTests
{
    private const ushort FirstGid = 10;
    private const ushort SecondGid = 20;
    private const short Kern = -50;

    [Fact]
    public void GetPairAdjustment_FindsKern_ThroughExtensionLookup()
    {
        var gpos = GposTable.Parse(BuildGpos(wrapInExtension: true));
        gpos.ShouldNotBeNull();
        gpos.GetPairAdjustment(FirstGid, SecondGid).ShouldBe((int)Kern);
        gpos.GetPairAdjustment(FirstGid, 999).ShouldBe(0); // unrelated pair
    }

    [Fact]
    public void GetPairAdjustment_StillWorks_ForDirectType2Lookup()
    {
        // The fix must not regress the common direct-lookup path.
        var gpos = GposTable.Parse(BuildGpos(wrapInExtension: false));
        gpos.ShouldNotBeNull();
        gpos.GetPairAdjustment(FirstGid, SecondGid).ShouldBe((int)Kern);
    }

    /// <summary>
    /// Build a minimal GPOS with one PairPos-format-1 subtable carrying a single
    /// (FirstGid, SecondGid) → XAdvance=Kern pair, optionally wrapped in a LookupType 9
    /// ExtensionPos. All internal offsets are relative to their spec-defined base.
    /// </summary>
    private static byte[] BuildGpos(bool wrapInExtension)
    {
        // ---- PairPos format 1 (self-contained block; offsets relative to its own start) ----
        // header(12) + Coverage(6) + PairSet(6)
        var pairPos = new byte[24];
        // posFormat=1, coverageOffset=12, valueFormat1=0x0004 (XAdvance), valueFormat2=0,
        // pairSetCount=1, pairSetOffset[0]=18
        WriteU16(pairPos, 0, 1);
        WriteU16(pairPos, 2, 12);
        WriteU16(pairPos, 4, 0x0004);
        WriteU16(pairPos, 6, 0);
        WriteU16(pairPos, 8, 1);
        WriteU16(pairPos, 10, 18);
        // Coverage format 1 @12: format=1, glyphCount=1, glyphs=[FirstGid]
        WriteU16(pairPos, 12, 1);
        WriteU16(pairPos, 14, 1);
        WriteU16(pairPos, 16, FirstGid);
        // PairSet @18: pairValueCount=1, record{ secondGlyph=SecondGid, value1.XAdvance=Kern }
        WriteU16(pairPos, 18, 1);
        WriteU16(pairPos, 20, SecondGid);
        WriteI16(pairPos, 22, Kern);

        // ---- Lookup subtable: either the PairPos directly, or an ExtensionPos wrapping it ----
        byte[] subtable;
        if (wrapInExtension)
        {
            // ExtensionPos format 1: posFormat=1, extensionLookupType=2, extensionOffset=8
            // (offset relative to the ExtensionPos start → PairPos placed right after the 8-byte header).
            subtable = new byte[8 + pairPos.Length];
            WriteU16(subtable, 0, 1);
            WriteU16(subtable, 2, 2);
            WriteU32(subtable, 4, 8);
            pairPos.CopyTo(subtable, 8);
        }
        else
        {
            subtable = pairPos;
        }

        // ---- Lookup: type (9 or 2), flag=0, subTableCount=1, subtableOffset (relative to lookup start) ----
        var lookupType = (ushort)(wrapInExtension ? 9 : 2);
        var lookup = new byte[8 + subtable.Length];
        WriteU16(lookup, 0, lookupType);
        WriteU16(lookup, 2, 0);
        WriteU16(lookup, 4, 1);
        WriteU16(lookup, 6, 8); // subtableOffset = 8 (subtable right after the 8-byte lookup header)
        subtable.CopyTo(lookup, 8);

        // ---- LookupList: lookupCount=1, lookupOffset[0]=4 (relative to LookupList start) ----
        var lookupList = new byte[4 + lookup.Length];
        WriteU16(lookupList, 0, 1);
        WriteU16(lookupList, 2, 4);
        lookup.CopyTo(lookupList, 4);

        // ---- GPOS header(10): major=1, minor=0, scriptListOffset, featureListOffset, lookupListOffset=10.
        // Script/Feature lists are unparsed by this slice; point them at the (empty) end of the buffer. ----
        var gpos = new byte[10 + lookupList.Length];
        WriteU16(gpos, 0, 1);
        WriteU16(gpos, 2, 0);
        WriteU16(gpos, 4, (ushort)gpos.Length); // scriptListOffset (unused)
        WriteU16(gpos, 6, (ushort)gpos.Length); // featureListOffset (unused)
        WriteU16(gpos, 8, 10);                   // lookupListOffset
        lookupList.CopyTo(gpos, 10);

        return gpos;
    }

    private static void WriteU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
    private static void WriteI16(byte[] b, int o, short v) => BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(o), v);
    private static void WriteU32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
}
