using System.Text;
using System.Text.Json;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

// Golden-fixture generator: shape (font, text, script, direction) cases with real
// HarfBuzz and write one JSON object per line (.jsonl). The engine's conformance
// tests replay these — glyph ids, clusters, and positions must match exactly
// (both read the same font tables; positions are in font units because the hb
// font scale is set to units-per-em).
//
// Usage:
//   HbFixtureGen <fontsDir> <outDir>
// Cases are currently the built-in list below; a --cases file can come later.
//
// Fixture line shape:
//   {"font":"DejaVuSans.ttf","text":"fi","script":"latn","dir":"ltr",
//    "glyphs":[[gid,cluster,xAdvance,yAdvance,xOffset,yOffset], ...]}
// xAdvance is hb's ABSOLUTE advance (base + GPOS); the engine test harness
// subtracts the glyph's hmtx advance to compare against XAdvanceDeltas.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: HbFixtureGen <fontsDir> <outDir>");
    return 1;
}

var fontsDir = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

// (font, text, script, rtl) — H1-era coverage: ligatures (fi/ffl), kerning (AV/Ta),
// mixed words, and a mark-attachment case. Extend per stage.
(string Font, string Text, string Script, bool Rtl)[] cases =
[
    ("DejaVuSans.ttf", "fi", "latn", false),
    ("DejaVuSans.ttf", "ffl", "latn", false),
    ("DejaVuSans.ttf", "Waffle", "latn", false),
    ("DejaVuSans.ttf", "AV", "latn", false),
    ("DejaVuSans.ttf", "Ta", "latn", false),
    ("DejaVuSans.ttf", "AVATAR", "latn", false),
    ("DejaVuSans.ttf", "office", "latn", false),
    ("DejaVuSans.ttf", "áé", "latn", false), // combining acute marks
];

var byFont = cases.GroupBy(c => c.Font, StringComparer.Ordinal);
foreach (var group in byFont)
{
    var fontPath = Path.Combine(fontsDir, group.Key);
    if (!File.Exists(fontPath))
    {
        Console.Error.WriteLine($"skip (missing font): {fontPath}");
        continue;
    }

    using var blob = Blob.FromFile(fontPath);
    using var face = new Face(blob, 0);
    using var font = new Font(face);
    // Font units in, font units out: scale = upem means positions need no rounding.
    font.SetScale(face.UnitsPerEm, face.UnitsPerEm);

    var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(group.Key) + ".jsonl");
    var sb = new StringBuilder();

    foreach (var c in group)
    {
        using var buffer = new Buffer();
        buffer.AddUtf16(c.Text);
        buffer.Direction = c.Rtl ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = Script.Parse(c.Script);
        buffer.Language = new Language("dflt");

        font.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("font", c.Font);
            w.WriteString("text", c.Text);
            w.WriteString("script", c.Script);
            w.WriteString("dir", c.Rtl ? "rtl" : "ltr");
            w.WriteStartArray("glyphs");
            for (var i = 0; i < infos.Length; i++)
            {
                w.WriteStartArray();
                w.WriteNumberValue(infos[i].Codepoint); // post-shaping = glyph id
                w.WriteNumberValue(infos[i].Cluster);
                w.WriteNumberValue(positions[i].XAdvance);
                w.WriteNumberValue(positions[i].YAdvance);
                w.WriteNumberValue(positions[i].XOffset);
                w.WriteNumberValue(positions[i].YOffset);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        sb.AppendLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    File.WriteAllText(outPath, sb.ToString());
    Console.WriteLine($"wrote {outPath}");
}

return 0;
