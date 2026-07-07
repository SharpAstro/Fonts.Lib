using System.Buffers.Binary;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Contextual lookups (GSUB 5/6/8, GPOS 3/7/8) driven through hand-assembled tables. The
/// chained-context + nested-substitution + mark-positioning pipeline is proven end-to-end
/// against real HarfBuzz by the <c>f</c>+combining-mark conformance fixtures; these cover
/// the paths those fixtures don't reach — the non-chained format 1 rule sets, backtrack /
/// lookahead gating, reverse chaining, and cursive attachment (which no DejaVu fixture
/// exercises). A real font backs the runner only for GDEF/hmtx; the glyph ids and layout
/// bytes are synthetic. All lookups use flag 0, so nothing is skipped and GDEF is inert.
/// </summary>
public class ContextualTests
{
    private const uint A = 100, B = 101, C = 102, X = 200, Z = 103;

    private static ShapingFont Font()
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf")));

    private static ShapeBuffer BufferOf(params uint[] glyphIds)
    {
        var buffer = new ShapeBuffer();
        buffer.AddText(new string('.', glyphIds.Length));
        for (var i = 0; i < glyphIds.Length; i++)
        {
            buffer.GlyphsMutable[i] = glyphIds[i];
            buffer.ClassesMutable[i] = (byte)GlyphClass.Base;
        }
        return buffer;
    }

    // Run a synthetic GSUB (empty script/feature lists + the given lookups) over the buffer,
    // applying lookup 0 across the whole run — its nested records reach the other lookups.
    private static void RunGsub(ShapeBuffer buffer, params (ushort Type, byte[] Subtable)[] lookups)
    {
        var table = LayoutTable.Parse(BuildLayout(lookups), extensionLookupType: 7, maxLookupType: 8)!;
        var runner = new LookupRunner(Font(), table, isSubstitution: true);
        runner.Run([new ShapePlan.PlannedLookup(0, ushort.MaxValue)], buffer);
    }

    [Fact]
    public void ContextFormat1_AppliesNestedSubstitution_OnMatch()
    {
        // Lookup 0: context [A, B, C] → apply lookup 1 at sequence index 1 (the B).
        // Lookup 1: single subst B → X.
        var context = BuildContextFormat1(first: (ushort)A, inputRest: [(ushort)B, (ushort)C], records: [(1, 1)]);
        var single = BuildSingleSubstFormat2(from: (ushort)B, to: (ushort)X);

        var buffer = BufferOf(A, B, C);
        RunGsub(buffer, (5, context), (1, single));

        buffer.GlyphIds.ToArray().ShouldBe([A, X, C]); // B substituted in context
    }

    [Fact]
    public void ContextFormat1_DoesNotApply_WhenSequenceDiffers()
    {
        var context = BuildContextFormat1(first: (ushort)A, inputRest: [(ushort)B, (ushort)C], records: [(1, 1)]);
        var single = BuildSingleSubstFormat2(from: (ushort)B, to: (ushort)X);

        var buffer = BufferOf(A, B, Z); // third glyph isn't C → rule must not fire
        RunGsub(buffer, (5, context), (1, single));

        buffer.GlyphIds.ToArray().ShouldBe([A, B, Z]);
    }

    [Fact]
    public void ChainedFormat3_RequiresBacktrackAndLookahead()
    {
        // backtrack [A], input [B], lookahead [C]; on match, single-subst B → X.
        var chained = BuildChainedFormat3(backtrack: [(ushort)A], input: [(ushort)B], lookahead: [(ushort)C], records: [(0, 1)]);
        var single = BuildSingleSubstFormat2(from: (ushort)B, to: (ushort)X);

        var match = BufferOf(A, B, C);
        RunGsub(match, (6, chained), (1, single));
        match.GlyphIds.ToArray().ShouldBe([A, X, C]);

        var noLookahead = BufferOf(A, B, Z); // lookahead C missing
        RunGsub(noLookahead, (6, chained), (1, single));
        noLookahead.GlyphIds.ToArray().ShouldBe([A, B, Z]);

        var noBacktrack = BufferOf(Z, B, C); // backtrack A missing
        RunGsub(noBacktrack, (6, chained), (1, single));
        noBacktrack.GlyphIds.ToArray().ShouldBe([Z, B, C]);
    }

    [Fact]
    public void ReverseChaining_SubstitutesInContext()
    {
        // ReverseChainSingleSubst: coverage {B}, backtrack {A}, lookahead {C}; B → X.
        var reverse = BuildReverseChain(coverage: [(ushort)B], backtrack: [[(ushort)A]], lookahead: [[(ushort)C]], substitutes: [(ushort)X]);

        var match = BufferOf(A, B, C);
        RunGsub(match, ((ushort)8, reverse));
        match.GlyphIds.ToArray().ShouldBe([A, X, C]);

        var noContext = BufferOf(Z, B, C); // wrong backtrack
        RunGsub(noContext, ((ushort)8, reverse));
        noContext.GlyphIds.ToArray().ShouldBe([Z, B, C]);
    }

    [Fact]
    public void Cursive_AlignsExitToEntry_AndChainsCrossStream()
    {
        // Cursive is standalone (no nested lookups), so drive the applier directly then run
        // the positioning-finish pass. Real gids give meaningful hmtx advances.
        var font = Font();
        var runner = new LookupRunner(font, font.Gpos!, isSubstitution: false);
        var gidA = font.Font.GetGlyphId('a');
        var gidB = font.Font.GetGlyphId('b');
        var advA = font.Font.Hmtx!.GetAdvanceWidth(gidA);

        const short exitX = 600, exitY = 120, entryX = 150, entryY = 40;
        var subtable = BuildCursive(
            (gidA, EntryX: null, EntryY: null, ExitX: exitX, ExitY: exitY),   // cur uses its exit
            (gidB, EntryX: entryX, EntryY: entryY, ExitX: null, ExitY: null)); // next uses its entry
        var lookup = new Lookup { Type = 3, Flags = LookupFlags.None, MarkFilteringSet = 0, Subtables = [], Digest = default };

        var buffer = new ShapeBuffer();
        buffer.AddText("..");
        buffer.GlyphsMutable[0] = gidA;
        buffer.GlyphsMutable[1] = gidB;
        var i = 0;
        GposApplier.Apply(runner, lookup, subtable, buffer, ref i, 0).ShouldBeTrue();
        GposApplier.Finish(font, buffer);

        // LTR: advance[cur] := exitX; next pulled back by entryX; next's y := exitY − entryY.
        buffer.XAdvanceDeltas[0].ShouldBe(exitX - advA);
        buffer.XOffsets[0].ShouldBe(0);
        buffer.XAdvanceDeltas[1].ShouldBe(-entryX);
        buffer.XOffsets[1].ShouldBe(-entryX);
        buffer.YOffsets[1].ShouldBe(exitY - entryY);
    }

    // ---- byte builders (offsets relative to each self-contained block) ----------------

    // GSUB/GPOS with empty script + feature lists and the given lookups (one subtable each).
    private static byte[] BuildLayout((ushort Type, byte[] Subtable)[] lookups)
    {
        // header(10) + scriptList(2, count 0) + featureList(2, count 0) + lookupList.
        const int headerLen = 10;
        var scriptListOffset = headerLen;
        var featureListOffset = scriptListOffset + 2;
        var lookupListOffset = featureListOffset + 2;

        // LookupList: count(2) + offsets(2 each) + each Lookup{type(2),flag(2),subCount(2),subOff(2),subtable}.
        var lookupBlocks = new byte[lookups.Length][];
        for (var l = 0; l < lookups.Length; l++)
        {
            var sub = lookups[l].Subtable;
            var block = new byte[8 + sub.Length];
            WriteU16(block, 0, lookups[l].Type);
            WriteU16(block, 2, 0);           // lookupFlag
            WriteU16(block, 4, 1);           // subTableCount
            WriteU16(block, 6, 8);           // subtableOffset (right after the 8-byte header)
            sub.CopyTo(block, 8);
            lookupBlocks[l] = block;
        }

        var listHeaderLen = 2 + lookups.Length * 2;
        var lookupList = new byte[listHeaderLen + lookupBlocks.Sum(b => b.Length)];
        WriteU16(lookupList, 0, (ushort)lookups.Length);
        var cursor = listHeaderLen;
        for (var l = 0; l < lookupBlocks.Length; l++)
        {
            WriteU16(lookupList, 2 + l * 2, (ushort)cursor);
            lookupBlocks[l].CopyTo(lookupList, cursor);
            cursor += lookupBlocks[l].Length;
        }

        var gsub = new byte[lookupListOffset + lookupList.Length];
        WriteU16(gsub, 0, 1);                            // major
        WriteU16(gsub, 2, 0);                            // minor
        WriteU16(gsub, 4, (ushort)scriptListOffset);
        WriteU16(gsub, 6, (ushort)featureListOffset);
        WriteU16(gsub, 8, (ushort)lookupListOffset);
        // scriptCount = 0, featureCount = 0 (the two u16 at those offsets are already zero).
        lookupList.CopyTo(gsub, lookupListOffset);
        return gsub;
    }

    private static byte[] BuildSingleSubstFormat2(ushort from, ushort to)
    {
        // format(2), coverageOffset(2), glyphCount(2), substituteGlyphIDs[1] + Coverage.
        var b = new byte[8 + 6];
        WriteU16(b, 0, 2);
        WriteU16(b, 2, 8); // coverage after the 8-byte header
        WriteU16(b, 4, 1); // glyphCount
        WriteU16(b, 6, to);
        WriteCoverage1(b, 8, from);
        return b;
    }

    private static byte[] BuildContextFormat1(ushort first, ushort[] inputRest, (ushort Seq, ushort Lookup)[] records)
    {
        // header(8) + Coverage + SequenceRuleSet(1 rule).
        var coverageOffset = 8;
        var ruleSetOffset = coverageOffset + 6;
        // rule: glyphCount(2), seqLookupCount(2), input[glyphCount-1] (2 each), records (4 each).
        var glyphCount = inputRest.Length + 1;
        var ruleLen = 4 + inputRest.Length * 2 + records.Length * 4;
        var ruleSetLen = 4 + ruleLen; // ruleCount(2) + ruleOffset(2) + rule
        var b = new byte[ruleSetOffset + ruleSetLen];
        WriteU16(b, 0, 1);                          // format
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, 1);                          // seqRuleSetCount
        WriteU16(b, 6, (ushort)ruleSetOffset);
        WriteCoverage1(b, coverageOffset, first);
        WriteU16(b, ruleSetOffset, 1);              // ruleCount
        WriteU16(b, ruleSetOffset + 2, 4);          // ruleOffset[0] (rule right after the 4-byte set header)
        var rule = ruleSetOffset + 4;
        WriteU16(b, rule, (ushort)glyphCount);
        WriteU16(b, rule + 2, (ushort)records.Length);
        for (var k = 0; k < inputRest.Length; k++) WriteU16(b, rule + 4 + k * 2, inputRest[k]);
        WriteRecords(b, rule + 4 + inputRest.Length * 2, records);
        return b;
    }

    private static byte[] BuildChainedFormat3(ushort[] backtrack, ushort[] input, ushort[] lookahead,
        (ushort Seq, ushort Lookup)[] records)
    {
        // format(2), backtrackCount(2), backtrackCov[], inputCount(2), inputCov[],
        // lookaheadCount(2), lookaheadCov[], recordCount(2), records[]. Then the coverage blocks.
        var covCount = backtrack.Length + input.Length + lookahead.Length;
        var headerLen = 2 + 2 + backtrack.Length * 2 + 2 + input.Length * 2 + 2 + lookahead.Length * 2 + 2 + records.Length * 4;
        var b = new byte[headerLen + covCount * 6];

        var pos = 0;
        WriteU16(b, pos, 3); pos += 2;
        var covCursor = headerLen;

        WriteU16(b, pos, (ushort)backtrack.Length); pos += 2;
        foreach (var g in backtrack) { WriteU16(b, pos, (ushort)covCursor); pos += 2; WriteCoverage1(b, covCursor, g); covCursor += 6; }
        WriteU16(b, pos, (ushort)input.Length); pos += 2;
        foreach (var g in input) { WriteU16(b, pos, (ushort)covCursor); pos += 2; WriteCoverage1(b, covCursor, g); covCursor += 6; }
        WriteU16(b, pos, (ushort)lookahead.Length); pos += 2;
        foreach (var g in lookahead) { WriteU16(b, pos, (ushort)covCursor); pos += 2; WriteCoverage1(b, covCursor, g); covCursor += 6; }
        WriteU16(b, pos, (ushort)records.Length); pos += 2;
        WriteRecords(b, pos, records);
        return b;
    }

    private static byte[] BuildReverseChain(ushort[] coverage, ushort[][] backtrack, ushort[][] lookahead, ushort[] substitutes)
    {
        // format(2), coverageOffset(2), backtrackCount(2), backtrackCov[], lookaheadCount(2),
        // lookaheadCov[], glyphCount(2), substitutes[]. Then coverage blocks.
        var headerLen = 2 + 2 + 2 + backtrack.Length * 2 + 2 + lookahead.Length * 2 + 2 + substitutes.Length * 2;
        var covBlocks = 1 + backtrack.Length + lookahead.Length;
        var b = new byte[headerLen + covBlocks * 6];
        var covCursor = headerLen;

        WriteU16(b, 0, 1); // format
        WriteU16(b, 2, (ushort)covCursor);
        WriteCoverageN(b, ref covCursor, coverage);

        var pos = 4;
        WriteU16(b, pos, (ushort)backtrack.Length); pos += 2;
        foreach (var set in backtrack) { WriteU16(b, pos, (ushort)covCursor); pos += 2; WriteCoverageN(b, ref covCursor, set); }
        WriteU16(b, pos, (ushort)lookahead.Length); pos += 2;
        foreach (var set in lookahead) { WriteU16(b, pos, (ushort)covCursor); pos += 2; WriteCoverageN(b, ref covCursor, set); }
        WriteU16(b, pos, (ushort)substitutes.Length); pos += 2;
        foreach (var g in substitutes) { WriteU16(b, pos, g); pos += 2; }
        return b;
    }

    private static byte[] BuildCursive(params (uint Glyph, int? EntryX, int? EntryY, int? ExitX, int? ExitY)[] records)
    {
        // format(2), coverageOffset(2), entryExitCount(2), records[{entry(2),exit(2)}], then anchors + coverage.
        var headerLen = 6 + records.Length * 4;
        var anchors = new List<(int Offset, short X, short Y)>();
        // Lay anchors after the header; coverage after anchors.
        var anchorArea = headerLen;
        var anchorBytes = new List<byte>();
        int PlaceAnchor(short x, short y)
        {
            var off = anchorArea + anchorBytes.Count;
            anchorBytes.AddRange([0, 1, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y]); // format 1
            return off;
        }

        var recAnchorOffsets = new (int Entry, int Exit)[records.Length];
        for (var r = 0; r < records.Length; r++)
        {
            var entry = records[r].EntryX is { } ex ? PlaceAnchor((short)ex, (short)records[r].EntryY!) : 0;
            var exit = records[r].ExitX is { } xx ? PlaceAnchor((short)xx, (short)records[r].ExitY!) : 0;
            recAnchorOffsets[r] = (entry, exit);
        }

        var coverageOffset = anchorArea + anchorBytes.Count;
        var b = new byte[coverageOffset + 4 + records.Length * 2];
        WriteU16(b, 0, 1);
        WriteU16(b, 2, (ushort)coverageOffset);
        WriteU16(b, 4, (ushort)records.Length);
        for (var r = 0; r < records.Length; r++)
        {
            WriteU16(b, 6 + r * 4, (ushort)recAnchorOffsets[r].Entry);
            WriteU16(b, 6 + r * 4 + 2, (ushort)recAnchorOffsets[r].Exit);
        }
        anchorBytes.CopyTo(b, anchorArea);
        // Coverage format 1 (records are the covered glyphs, in order).
        WriteU16(b, coverageOffset, 1);
        WriteU16(b, coverageOffset + 2, (ushort)records.Length);
        for (var r = 0; r < records.Length; r++) WriteU16(b, coverageOffset + 4 + r * 2, (ushort)records[r].Glyph);
        return b;
    }

    private static void WriteRecords(byte[] b, int offset, (ushort Seq, ushort Lookup)[] records)
    {
        for (var r = 0; r < records.Length; r++)
        {
            WriteU16(b, offset + r * 4, records[r].Seq);
            WriteU16(b, offset + r * 4 + 2, records[r].Lookup);
        }
    }

    private static void WriteCoverage1(byte[] b, int offset, ushort glyph)
    {
        WriteU16(b, offset, 1);
        WriteU16(b, offset + 2, 1);
        WriteU16(b, offset + 4, glyph);
    }

    private static void WriteCoverageN(byte[] b, ref int offset, ushort[] glyphs)
    {
        WriteU16(b, offset, 1);
        WriteU16(b, offset + 2, (ushort)glyphs.Length);
        for (var g = 0; g < glyphs.Length; g++) WriteU16(b, offset + 4 + g * 2, glyphs[g]);
        offset += 4 + glyphs.Length * 2;
    }

    private static void WriteU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
}
