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

    private static void WriteU24(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
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
        for (var i = 0; i < bytes.Count; i += 16)
        {
            sb.Append("        ");
            for (var j = i; j < i + 16 && j < bytes.Count; j++)
                sb.Append("0x").Append(bytes[j].ToString("X2", CultureInfo.InvariantCulture)).Append(", ");
            sb.Length--; // drop the trailing space, keep the comma
            sb.Append('\n');
        }
        sb.Append("    ];\n");
        sb.Append("}\n");

        // Always LF, no BOM — deterministic across platforms.
        File.WriteAllText(Path.Combine(outDir, className + ".g.cs"), sb.ToString(), new UTF8Encoding(false));
    }
}
