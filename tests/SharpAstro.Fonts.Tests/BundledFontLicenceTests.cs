using System.Text;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Every font file this repository ships — test fixtures and the web demo's <c>wwwroot</c> — is
/// redistributed, so each one needs a licence that permits that. These checks read each face's
/// own <c>name</c> IDs 13 (License Description) and 14 (License Info URL) rather than trusting a
/// file name or a recollection of where the file came from.
///
/// <para>Anything not covered by <see cref="StatesNoLicence"/> must state a licence we recognise
/// as redistributable, so a new font dropped into either directory fails here until someone
/// accounts for it.</para>
/// </summary>
public class BundledFontLicenceTests
{
    /// <summary>Substrings that identify a licence permitting redistribution. Matched
    /// case-insensitively against name IDs 13 and 14 together.</summary>
    private static readonly string[] Redistributable =
    [
        "open font license", "openfontlicense", "scripts.sil.org/ofl", // SIL OFL 1.1
        "apache license",                                              // Apache 2.0
        "bitstream vera",                                              // DejaVu / Vera
        "public domain",
    ];

    /// <summary>
    /// Faces that state nothing in name IDs 13/14, listed so the silence is a recorded decision
    /// rather than an unnoticed gap. All five are small glyph subsets extracted from PDFs and
    /// have been fixtures since the repository's first commit; each exists because it reproduces
    /// a specific parser defect that no freely-licensed font in the corpus reproduces.
    ///
    /// <para>Subsetting drops the <c>name</c> table's licensing records along with everything
    /// else the PDF did not need, so the absence here is not evidence that the original was
    /// unlicensed — but for <c>Tahoma_subset</c> and <c>XXTIIT_Arial_subset</c> the originals are
    /// proprietary (Microsoft / Monotype). Whether a few dozen KB of subset outlines in a public
    /// test corpus is acceptable is a licensing judgement, not a technical one; it is flagged in
    /// TODO.md rather than settled here.</para>
    /// </summary>
    private static readonly Dictionary<string, string> StatesNoLicence = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D011A_subset.ttf"] = "Canon EOS450D manual key-caps; cmap fmt4 length overruns the table",
        ["ISOCPEUR_subset.ttf"] = "AutoCAD ISOCPEUR; PDF subset-font loading",
        ["Merida.ttf"] = "provenance never established — kept out of the web demo for that reason",
        ["Tahoma_subset.ttf"] = "PROPRIETARY original (Microsoft); PDF subset regression",
        ["XXTIIT_Arial_subset.ttf"] = "PROPRIETARY original (Monotype); small-size hinted baselines",
    };

    public static TheoryData<string> BundledFonts()
    {
        var data = new TheoryData<string>();
        foreach (var path in EnumerateBundledFonts()) data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(BundledFonts))]
    public void EveryBundledFace_StatesARedistributableLicence(string path)
    {
        var file = Path.GetFileName(path);
        var font = OpenTypeFont.LoadFromFile(path);
        var name = font.Name;
        name.ShouldNotBeNull($"{file} has no 'name' table, so it makes no licensing claim at all");

        var stated = $"{name.License} {name.LicenseUrl}".Trim();

        if (StatesNoLicence.TryGetValue(file, out var why))
        {
            // Pinned in both directions: if one of these ever gains a licence statement, the
            // exception should go rather than linger.
            stated.ShouldBeEmpty($"{file} now states a licence — drop it from StatesNoLicence "
                               + $"(recorded reason: {why})");
            return;
        }

        stated.ShouldNotBeEmpty(
            $"{file} states no licence in name IDs 13/14. Establish it from the source before "
          + "redistributing, or record it in StatesNoLicence with the reason it is here.");

        Redistributable.Any(k => stated.Contains(k, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue($"{file} states a licence we do not recognise as redistributable:\n"
                        + $"  ID 13: {name.License}\n  ID 14: {name.LicenseUrl}");
    }

    /// <summary>The web demo is a published site rather than a test corpus, so it holds to the
    /// stricter rule: every face it serves states a licence permitting redistribution, with no
    /// exceptions list. This is what keeps <c>Merida.ttf</c> out of it.</summary>
    [Fact]
    public void WebDemoFonts_AreAllExplicitlyLicensed()
    {
        if (!Directory.Exists(WebFontsDir)) { Assert.Skip("web font set not present"); return; }

        var faces = Directory.EnumerateFiles(WebFontsDir)
            .Where(IsFont).OrderBy(p => p, StringComparer.Ordinal).ToList();
        faces.ShouldNotBeEmpty();

        foreach (var path in faces)
        {
            var name = OpenTypeFont.LoadFromFile(path).Name;
            var stated = $"{name?.License} {name?.LicenseUrl}";
            Redistributable.Any(k => stated.Contains(k, StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue($"{Path.GetFileName(path)} is served by the web demo but does not "
                            + $"state a redistributable licence:\n  ID 13: {name?.License}");
        }
    }

    /// <summary>Not an assertion — a readable inventory, written beside the test binary so the
    /// audit can be reviewed rather than merely passed.</summary>
    [Fact]
    public void WriteLicenceInventory()
    {
        var sb = new StringBuilder();
        foreach (var path in EnumerateBundledFonts())
        {
            var name = OpenTypeFont.LoadFromFile(path).Name;
            sb.Append(Path.GetFileName(path)).Append('\n')
              .Append("    13: ").Append(Squash(name?.License) ?? "(none)").Append('\n')
              .Append("    14: ").Append(Squash(name?.LicenseUrl) ?? "(none)").Append('\n');
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "font-licences.txt"), sb.ToString());
    }

    private static string? Squash(string? s) =>
        s is null ? null
        : s.ReplaceLineEndings(" ") is var flat && flat.Length > 160 ? flat[..160] + "…" : flat;

    private static bool IsFont(string p) =>
        p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
     || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateBundledFonts()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(Fixtures.Path("x"))!, WebFontsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir).Where(IsFont)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return path;
            }
        }
    }

    /// <summary>The web demo's font directory, reached from the test binary via the repo root.</summary>
    private static string WebFontsDir => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "SharpAstro.Fonts.Web", "wwwroot", "fonts"));
}
