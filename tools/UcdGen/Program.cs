using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UcdGen;

/// <summary>
/// Offline, dev-only generator: reads the vendored UCD snapshot under
/// <c>data/ucd/&lt;version&gt;/</c> and emits packed RVA property tables into
/// <c>src/SharpAstro.Fonts.Shaping/Ucd/*.g.cs</c>. Output is deterministic — the same input
/// yields byte-identical files, so a no-op regeneration produces no diff. Run from the repo
/// root (or pass the repo root as the first argument):
/// <code>dotnet run --project tools/UcdGen</code>
/// </summary>
internal static class Program
{
    private const string UcdVersion = "17.0.0";

    private static int Main(string[] args)
    {
        var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("UcdGen: could not locate the repo root (no SharpAstro.Fonts.slnx found above the tool).");
            return 1;
        }

        var dataDir = Path.Combine(repoRoot, "data", "ucd", UcdVersion);
        var outDir = Path.Combine(repoRoot, "src", "SharpAstro.Fonts.Shaping", "Ucd");
        Directory.CreateDirectory(outDir);

        EmitCombiningClass(dataDir, outDir);
        EmitJoining(dataDir, outDir);
        EmitMirroring(dataDir, outDir);
        EmitScript(dataDir, outDir);
        EmitBidiClass(dataDir, outDir);
        EmitBidiBrackets(dataDir, outDir);
        return 0;
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SharpAstro.Fonts.slnx")))
                return dir.FullName;
        return null;
    }

    // ---- UnicodeData.txt: Canonical_Combining_Class is field 3 (0-based); keep nonzero only.
    // Range markers (<..., First>/<..., Last>) all carry CCC 0, so ignoring range semantics
    // here is safe — every combining mark is listed on its own line.
    private static void EmitCombiningClass(string dataDir, string outDir)
    {
        var file = Path.Combine(dataDir, "UnicodeData.txt");
        var entries = new List<(uint Cp, byte Val)>();
        foreach (var fields in ReadRecords(file))
        {
            var ccc = byte.Parse(fields[3], CultureInfo.InvariantCulture);
            if (ccc != 0)
                entries.Add((ParseHex(fields[0]), ccc));
        }
        WriteRangeTable(outDir, "CanonicalCombiningClass", file, "Ranges", Coalesce(entries), "canonical-combining-class");
    }

    // ---- ArabicShaping.txt: Joining_Type is field 2. U/T/D/R/L/C map to the JoiningType enum
    // byte order in Ucd/Joining.cs (NonJoining, Transparent, DualJoining, RightJoining,
    // LeftJoining, JoinCausing). Codepoints absent here default by general category at runtime.
    private static void EmitJoining(string dataDir, string outDir)
    {
        var file = Path.Combine(dataDir, "ArabicShaping.txt");
        var entries = new List<(uint Cp, byte Val)>();
        foreach (var fields in ReadRecords(file))
        {
            var jt = fields[2] switch
            {
                "U" => (byte)0,
                "T" => (byte)1,
                "D" => (byte)2,
                "R" => (byte)3,
                "L" => (byte)4,
                "C" => (byte)5,
                var other => throw new FormatException($"Unknown Joining_Type '{other}' for U+{fields[0]}"),
            };
            entries.Add((ParseHex(fields[0]), jt));
        }
        WriteRangeTable(outDir, "Joining", file, "Ranges", Coalesce(entries), "joining-type");
    }

    // ---- BidiMirroring.txt: "code; mirror # name". A flat codepoint→codepoint map.
    private static void EmitMirroring(string dataDir, string outDir)
    {
        var file = Path.Combine(dataDir, "BidiMirroring.txt");
        var pairs = new List<(uint Cp, uint Mirror)>();
        foreach (var fields in ReadRecords(file))
            pairs.Add((ParseHex(fields[0]), ParseHex(fields[1])));
        pairs.Sort((a, b) => a.Cp.CompareTo(b.Cp));

        var bytes = new List<byte>(pairs.Count * 6);
        foreach (var (cp, mirror) in pairs)
        {
            WriteU24(bytes, cp);
            WriteU24(bytes, mirror);
        }
        WriteBlob(outDir, "BidiMirroring", file, "Pairs", bytes, pairs.Count, "mirror-pair");
    }

    // ---- Scripts.txt + PropertyValueAliases.txt: codepoint → OpenType script tag.
    // Scripts.txt gives long names over "start..end" ranges; PropertyValueAliases 'sc' lines map
    // long → ISO 15924 short code, which lowercases to the OT script tag (arab, latn, hebr, …).
    // The tag is stored as the engine Tag's big-endian packed uint. ISO⇒OT exceptions (e.g. Nkoo
    // vs "nko ", Hira vs "kana") are not applied — they only affect scripts the engine doesn't
    // specially shape, which fall back to DFLT.
    private static void EmitScript(string dataDir, string outDir)
    {
        var longToShort = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fields in ReadRecords(Path.Combine(dataDir, "PropertyValueAliases.txt")))
            if (fields.Length >= 3 && fields[0] == "sc")
                longToShort[fields[2]] = fields[1];

        var file = Path.Combine(dataDir, "Scripts.txt");
        var ranges = new List<(uint Start, uint End, uint Val)>();
        foreach (var fields in ReadRecords(file))
        {
            var (start, end) = ParseRange(fields[0]);
            if (!longToShort.TryGetValue(fields[1], out var shortCode))
                throw new FormatException($"No 'sc' alias for script '{fields[1]}'");
            ranges.Add((start, end, PackTag(shortCode)));
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var coalesced = CoalesceRanges(ranges);

        var rangeBytes = new List<byte>(coalesced.Count * 10);
        foreach (var (start, end, val) in coalesced)
        {
            WriteU24(rangeBytes, start);
            WriteU24(rangeBytes, end);
            WriteU32(rangeBytes, val);
        }

        // High-bit page index (the pre-generated two-stage-trie technique): for each 256-codepoint
        // page, the index of the first range that reaches into it. Script.Get dispatches on cp>>8
        // to this index and scans only the handful of ranges overlapping that page instead of
        // binary-searching all ~1000 ranges — O(1) for the general (non-Latin) itemizer path.
        // (ScriptItemizer's Latin fast path skips script lookup entirely for plain UI text.)
        if (coalesced.Count > ushort.MaxValue)
            throw new InvalidOperationException("Script range count exceeds the u16 page-index width.");
        var maxEnd = coalesced.Count > 0 ? coalesced[^1].End : 0; // coalesced is sorted, non-overlapping
        var pageCount = (int)(maxEnd >> 8) + 1;
        var pageBytes = new List<byte>(pageCount * 2);
        var ri = 0;
        for (var p = 0; p < pageCount; p++)
        {
            var pageStart = (uint)p << 8;
            while (ri < coalesced.Count && coalesced[ri].End < pageStart) ri++;
            WriteU16(pageBytes, (ushort)ri);
        }

        WriteScriptTables(outDir, file, rangeBytes, coalesced.Count, pageBytes, pageCount);
    }

    // Bidi_Class short + long names → the byte enum in Ucd/Bidi.cs (BidiClass). DerivedBidiClass.txt
    // uses short names in data lines and long names in the @missing default lines, so both map here.
    private static readonly Dictionary<string, byte> BidiClassCode = new(StringComparer.Ordinal)
    {
        ["L"] = 0, ["Left_To_Right"] = 0,
        ["R"] = 1, ["Right_To_Left"] = 1,
        ["AL"] = 2, ["Arabic_Letter"] = 2,
        ["EN"] = 3, ["European_Number"] = 3,
        ["ES"] = 4, ["European_Separator"] = 4,
        ["ET"] = 5, ["European_Terminator"] = 5,
        ["AN"] = 6, ["Arabic_Number"] = 6,
        ["CS"] = 7, ["Common_Separator"] = 7,
        ["NSM"] = 8, ["Nonspacing_Mark"] = 8,
        ["BN"] = 9, ["Boundary_Neutral"] = 9,
        ["B"] = 10, ["Paragraph_Separator"] = 10,
        ["S"] = 11, ["Segment_Separator"] = 11,
        ["WS"] = 12, ["White_Space"] = 12,
        ["ON"] = 13, ["Other_Neutral"] = 13,
        ["LRE"] = 14, ["Left_To_Right_Embedding"] = 14,
        ["LRO"] = 15, ["Left_To_Right_Override"] = 15,
        ["RLE"] = 16, ["Right_To_Left_Embedding"] = 16,
        ["RLO"] = 17, ["Right_To_Left_Override"] = 17,
        ["PDF"] = 18, ["Pop_Directional_Format"] = 18,
        ["LRI"] = 19, ["Left_To_Right_Isolate"] = 19,
        ["RLI"] = 20, ["Right_To_Left_Isolate"] = 20,
        ["FSI"] = 21, ["First_Strong_Isolate"] = 21,
        ["PDI"] = 22, ["Pop_Directional_Isolate"] = 22,
    };

    // ---- DerivedBidiClass.txt: codepoint → Bidi_Class (UAX #9). The file carries @missing default
    // ranges in COMMENTS (the global 0000..10FFFF; Left_To_Right, then RTL/AL/ET block overrides),
    // which ReadRecords strips — so scan raw for those first, then apply the assigned data ranges on
    // top. We emit only non-L ranges; L is the notFound default (matches the global @missing).
    private static void EmitBidiClass(string dataDir, string outDir)
    {
        var file = Path.Combine(dataDir, "DerivedBidiClass.txt");
        var cls = new byte[0x110000]; // 0 == L

        foreach (var raw in File.ReadLines(file))
        {
            var at = raw.IndexOf("@missing:", StringComparison.Ordinal);
            if (at < 0) continue;
            var parts = raw[(at + 9)..].Split(';');
            var (start, end) = ParseRange(parts[0].Trim());
            var val = BidiClassCode[parts[1].Trim()];
            for (var cp = start; cp <= end; cp++) cls[cp] = val;
        }

        foreach (var fields in ReadRecords(file))
        {
            var (start, end) = ParseRange(fields[0]);
            var val = BidiClassCode[fields[1]];
            for (var cp = start; cp <= end; cp++) cls[cp] = val;
        }

        var ranges = new List<(uint Start, uint End, byte Val)>();
        uint runStart = 0;
        for (uint cp = 1; cp <= 0x10FFFF; cp++)
        {
            if (cls[cp] == cls[runStart]) continue;
            if (cls[runStart] != 0) ranges.Add((runStart, cp - 1, cls[runStart]));
            runStart = cp;
        }
        if (cls[runStart] != 0) ranges.Add((runStart, 0x10FFFF, cls[runStart]));

        WriteRangeTable(outDir, "Bidi", file, "Ranges", ranges, "bidi-class");
    }

    // ---- BidiBrackets.txt: "code; paired; type(o|c) # name". Packs paired codepoint (21 bits) with
    // the open flag in bit 23, keyed by codepoint — the Bidi_Paired_Bracket(_Type) UAX #9 rule N0 needs.
    private static void EmitBidiBrackets(string dataDir, string outDir)
    {
        var file = Path.Combine(dataDir, "BidiBrackets.txt");
        var pairs = new List<(uint Cp, uint Packed)>();
        foreach (var fields in ReadRecords(file))
        {
            var paired = ParseHex(fields[1]);
            var open = fields[2] == "o";
            pairs.Add((ParseHex(fields[0]), paired | (open ? 0x800000u : 0u)));
        }
        pairs.Sort((a, b) => a.Cp.CompareTo(b.Cp));

        var bytes = new List<byte>(pairs.Count * 6);
        foreach (var (cp, packed) in pairs)
        {
            WriteU24(bytes, cp);
            WriteU24(bytes, packed);
        }
        WriteBlob(outDir, "BidiBrackets", file, "Pairs", bytes, pairs.Count, "bracket-pair");
    }

    // ---- shared helpers ----

    /// <summary>Yield each non-empty, comment-stripped UCD line split on ';' with fields trimmed.</summary>
    private static IEnumerable<string[]> ReadRecords(string file)
    {
        foreach (var raw in File.ReadLines(file))
        {
            var hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
                continue;
            var fields = line.Split(';');
            for (var i = 0; i < fields.Length; i++)
                fields[i] = fields[i].Trim();
            yield return fields;
        }
    }

    private static uint ParseHex(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Parse a UCD codepoint field that is either "XXXX" or a "XXXX..YYYY" range.</summary>
    private static (uint Start, uint End) ParseRange(string field)
    {
        var dots = field.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
        {
            var cp = ParseHex(field);
            return (cp, cp);
        }
        return (ParseHex(field[..dots]), ParseHex(field[(dots + 2)..]));
    }

    /// <summary>Pack a 4-char ISO 15924 script code as the engine Tag's big-endian uint, lowercased
    /// to the OpenType script tag (e.g. "Arab" → 0x61726162 = "arab").</summary>
    private static uint PackTag(string isoCode)
    {
        if (isoCode.Length != 4)
            throw new FormatException($"Script code '{isoCode}' is not 4 characters");
        uint value = 0;
        foreach (var c in isoCode)
        {
            var lower = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            value = (value << 8) | (byte)lower;
        }
        return value;
    }

    /// <summary>Sort entries by codepoint and merge runs of adjacent codepoints sharing a value
    /// into inclusive ranges.</summary>
    private static List<(uint Start, uint End, byte Val)> Coalesce(List<(uint Cp, byte Val)> entries)
    {
        entries.Sort((a, b) => a.Cp.CompareTo(b.Cp));
        var ranges = new List<(uint Start, uint End, byte Val)>();
        foreach (var (cp, val) in entries)
        {
            if (ranges.Count > 0)
            {
                var last = ranges[^1];
                if (cp == last.End)
                    continue; // duplicate codepoint (last definition already recorded)
                if (val == last.Val && cp == last.End + 1)
                {
                    ranges[^1] = (last.Start, cp, val);
                    continue;
                }
            }
            ranges.Add((cp, cp, val));
        }
        return ranges;
    }

    private static void WriteRangeTable(string outDir, string className, string sourceFile, string propName,
        List<(uint Start, uint End, byte Val)> ranges, string entryKind)
    {
        var bytes = new List<byte>(ranges.Count * 7);
        foreach (var (start, end, val) in ranges)
        {
            WriteU24(bytes, start);
            WriteU24(bytes, end);
            bytes.Add(val);
        }
        WriteBlob(outDir, className, sourceFile, propName, bytes, ranges.Count, entryKind);
    }

    private static void WriteU16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static void WriteU24(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
    }

    private static void WriteU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }

    /// <summary>Merge pre-sorted ranges that share a value and touch or overlap (used for the
    /// Script table, whose values are 32-bit tags rather than single bytes).</summary>
    private static List<(uint Start, uint End, uint Val)> CoalesceRanges(List<(uint Start, uint End, uint Val)> ranges)
    {
        var result = new List<(uint Start, uint End, uint Val)>();
        foreach (var r in ranges)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (r.Val == last.Val && r.Start <= last.End + 1)
                {
                    result[^1] = (last.Start, Math.Max(last.End, r.End), last.Val);
                    continue;
                }
            }
            result.Add(r);
        }
        return result;
    }

    private static void WriteBlob(string outDir, string className, string sourceFile, string propName,
        List<byte> bytes, int entryCount, string entryKind)
    {
        var srcName = Path.GetFileName(sourceFile);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourceFile)))[..12];

        var sb = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        sb.Append("// <auto-generated/>\n");
        sb.Append(invariant, $"// Generated by tools/UcdGen from UCD {UcdVersion} {srcName} (sha256 {hash}).\n");
        sb.Append(invariant, $"// {entryCount} {entryKind} entries, {bytes.Count} bytes. Do not edit — regenerate with `dotnet run --project tools/UcdGen`.\n");
        sb.Append('\n');
        sb.Append("namespace SharpAstro.Fonts.Shaping.Ucd;\n");
        sb.Append('\n');
        sb.Append(invariant, $"internal static partial class {className}\n");
        sb.Append("{\n");
        sb.Append(invariant, $"    internal static global::System.ReadOnlySpan<byte> {propName} =>\n");
        sb.Append("    [\n");
        AppendHexRows(sb, bytes);
        sb.Append("    ];\n");
        sb.Append("}\n");

        // Always LF, no BOM — deterministic across platforms.
        File.WriteAllText(Path.Combine(outDir, className + ".g.cs"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Append <paramref name="bytes"/> as 16-per-row "0xNN, " literals (shared by the
    /// single- and multi-property writers so their output stays byte-identical).</summary>
    private static void AppendHexRows(StringBuilder sb, List<byte> bytes)
    {
        for (var i = 0; i < bytes.Count; i += 16)
        {
            sb.Append("        ");
            for (var j = i; j < i + 16 && j < bytes.Count; j++)
                sb.Append("0x").Append(bytes[j].ToString("X2", CultureInfo.InvariantCulture)).Append(", ");
            sb.Length--; // drop the trailing space, keep the comma
            sb.Append('\n');
        }
    }

    // Script emits TWO blobs into one partial class: the wide-range table and the page index that
    // dispatches into it (see EmitScript). Bespoke rather than the single-property WriteBlob so the
    // two properties live together in Script.g.cs.
    private static void WriteScriptTables(string outDir, string sourceFile,
        List<byte> rangeBytes, int rangeCount, List<byte> pageBytes, int pageCount)
    {
        var srcName = Path.GetFileName(sourceFile);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourceFile)))[..12];

        var sb = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        sb.Append("// <auto-generated/>\n");
        sb.Append(invariant, $"// Generated by tools/UcdGen from UCD {UcdVersion} {srcName} (sha256 {hash}).\n");
        sb.Append(invariant, $"// Ranges: {rangeCount} script-range entries, {rangeBytes.Count} bytes.\n");
        sb.Append(invariant, $"// PageIndex: {pageCount} page entries (cp>>8, u16 range index), {pageBytes.Count} bytes.\n");
        sb.Append("// Do not edit — regenerate with `dotnet run --project tools/UcdGen`.\n");
        sb.Append('\n');
        sb.Append("namespace SharpAstro.Fonts.Shaping.Ucd;\n");
        sb.Append('\n');
        sb.Append("internal static partial class Script\n");
        sb.Append("{\n");
        sb.Append("    internal static global::System.ReadOnlySpan<byte> Ranges =>\n");
        sb.Append("    [\n");
        AppendHexRows(sb, rangeBytes);
        sb.Append("    ];\n");
        sb.Append('\n');
        sb.Append("    internal static global::System.ReadOnlySpan<byte> PageIndex =>\n");
        sb.Append("    [\n");
        AppendHexRows(sb, pageBytes);
        sb.Append("    ];\n");
        sb.Append("}\n");

        File.WriteAllText(Path.Combine(outDir, "Script.g.cs"), sb.ToString(), new UTF8Encoding(false));
    }
}
