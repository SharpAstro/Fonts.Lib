using System.Text.Json;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>One shaped glyph from a HarfBuzz golden fixture (font units, hb conventions:
/// XAdvance is the ABSOLUTE advance — base hmtx + GPOS; the engine reports deltas, so
/// conformance comparisons subtract the hmtx advance).</summary>
public readonly record struct HbGlyph(uint Gid, int Cluster, int XAdvance, int YAdvance, int XOffset, int YOffset);

/// <summary>One (font, text, script, direction) → glyphs case shaped by real HarfBuzz.</summary>
public sealed record HbCase(string Font, string Text, string Script, bool Rtl, IReadOnlyList<HbGlyph> Glyphs);

/// <summary>
/// Loader for the <c>HbFixtures/*.jsonl</c> golden fixtures produced by
/// <c>tools/HbFixtureGen</c> (real HarfBuzz output, checked in — CI never runs
/// native HarfBuzz). H0 asserts the fixtures parse and are structurally sane;
/// H1+ replays them through the engine and compares glyph-for-glyph.
///
/// <para>Known divergence to keep in mind when authoring cases: HarfBuzz
/// normalizes input (e.g. composes <c>a + U+0301</c> to a precomposed <c>á</c>
/// glyph when the font has one); the engine assumes NFC input and does no
/// normalization (plan: "no normalization pass in v1"). Mark-positioning cases
/// must therefore use base+mark pairs without precomposed forms, or pre-compose
/// the fixture text.</para>
/// </summary>
public static class HbFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "HbFixtures");

    public static IEnumerable<HbCase> LoadAll()
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(Dir, "*.jsonl"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                yield return Parse(line);
            }
        }
    }

    private static HbCase Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var glyphs = new List<HbGlyph>();
        foreach (var g in root.GetProperty("glyphs").EnumerateArray())
        {
            glyphs.Add(new HbGlyph(
                g[0].GetUInt32(), g[1].GetInt32(), g[2].GetInt32(),
                g[3].GetInt32(), g[4].GetInt32(), g[5].GetInt32()));
        }
        return new HbCase(
            root.GetProperty("font").GetString()!,
            root.GetProperty("text").GetString()!,
            root.GetProperty("script").GetString()!,
            root.GetProperty("dir").GetString() == "rtl",
            glyphs);
    }
}

public class HbFixtureTests
{
    [Fact]
    public void Fixtures_AreCheckedIn_AndStructurallySane()
    {
        var cases = HbFixtures.LoadAll().ToList();
        cases.Count.ShouldBeGreaterThan(5, "HbFixtures/*.jsonl should be checked in (regen: tools/HbFixtureGen)");

        foreach (var c in cases)
        {
            c.Glyphs.Count.ShouldBeGreaterThan(0);
            File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", c.Font))
                .ShouldBeTrue($"fixture font {c.Font} must be a bundled test font");
            foreach (var g in c.Glyphs)
            {
                // Clusters index the source text in UTF-16 units.
                g.Cluster.ShouldBeInRange(0, c.Text.Length - 1);
            }
        }
    }

    [Fact]
    public void Fixtures_ShowRealShaping_LigatureAndKerning()
    {
        // Guards fixture regeneration: if these stop holding, the fixture set no
        // longer exercises what H1 is being built against.
        var cases = HbFixtures.LoadAll().ToList();

        var fi = cases.Single(c => c.Text == "fi");
        fi.Glyphs.Count.ShouldBe(1); // f+i → one ligature glyph

        var avatar = cases.Single(c => c.Text == "AVATAR");
        var aAdvances = avatar.Glyphs.Where(g => g.Gid == avatar.Glyphs[0].Gid)
            .Select(g => g.XAdvance).Distinct().ToList();
        aAdvances.Count.ShouldBeGreaterThan(1, "the same 'A' glyph should carry different kerned advances per pair");
    }
}
