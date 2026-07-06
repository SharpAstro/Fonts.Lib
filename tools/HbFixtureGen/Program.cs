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

// (font, text, script, rtl) — H1 coverage: ligatures (fi/ffl), kerning (AV/Ta), mixed
// words. H2 coverage: base+combining-mark sequences whose bases have NO precomposed form,
// so HarfBuzz keeps them as base+mark and actually runs GPOS mark positioning. (A
// precomposed pair like "a"+U+0301 gets composed to a single "á" glyph — see HbFixtures
// remarks — and the no-normalization engine can't match that, so such cases are
// deliberately avoided.) Bases: 'q' has no Latin precomposed forms at all; 'f'/'x' have
// none for these marks. Marks are typed in canonical (CCC-ascending) order.
(string Font, string Text, string Script, bool Rtl)[] cases =
[
    ("DejaVuSans.ttf", "fi", "latn", false),
    ("DejaVuSans.ttf", "ffl", "latn", false),
    ("DejaVuSans.ttf", "Waffle", "latn", false),
    ("DejaVuSans.ttf", "AV", "latn", false),
    ("DejaVuSans.ttf", "Ta", "latn", false),
    ("DejaVuSans.ttf", "AVATAR", "latn", false),
    ("DejaVuSans.ttf", "office", "latn", false),
    ("DejaVuSans.ttf", "q́", "latn", false),         // q + acute above       (mark-to-base)
    ("DejaVuSans.ttf", "q̣", "latn", false),         // q + dot below         (mark-to-base, below anchor)
    ("DejaVuSans.ttf", "q̣́", "latn", false),   // q + dot-below + acute (two mark-to-base, opposite sides)
    ("DejaVuSans.ttf", "q̣́", "latn", false),   // q + acute + dot-below (NON-canonical order → CCC reorder)
    ("DejaVuSans.ttf", "x̄́", "latn", false),   // x + macron + acute    (mark stacking → mark-to-mark)
    // Contextual: after the ascender 'f', DejaVu chained-context-substitutes the combining
    // mark to a raised variant glyph (GSUB 6 -> nested single subst). No composition (no
    // precomposed "f"+accent), so HarfBuzz keeps base+mark and the variant shows up.
    ("DejaVuSans.ttf", "f́", "latn", false),         // f + acute      (chained context)
    ("DejaVuSans.ttf", "f̀", "latn", false),         // f + grave      (chained context)
    ("DejaVuSans.ttf", "f̂", "latn", false),         // f + circumflex (chained context)
    // H4 Arabic joining: DejaVu's 'arab' script has init/medi/fina (isolated = the cmap base).
    // The shaper picks each letter's positional form; HarfBuzz substitutes the same glyphs.
    // Text is literal, logical order (beh U+0628, lam U+0644, alef U+0627, meem U+0645).
    ("DejaVuSans.ttf", "بب", "arab", true),                 // beh+beh          → init, fina
    ("DejaVuSans.ttf", "ببب", "arab", true),           // beh×3            → init, medi, fina
    ("DejaVuSans.ttf", "لا", "arab", true),                 // lam+alef         → lam-alef (rlig/forms)
    ("DejaVuSans.ttf", "سلام", "arab", true),     // seen-lam-alef-meem "salaam" → init, medi, fina, isol
    // H4 Hebrew RTL (non-joining): pure reversal + cmap, no positional forms. Proves the RTL path.
    ("DejaVuSans.ttf", "אב", "hebr", true),                 // alef+bet
    ("DejaVuSans.ttf", "שלום", "hebr", true),     // shin-lamed-vav-finalmem "shalom"
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
