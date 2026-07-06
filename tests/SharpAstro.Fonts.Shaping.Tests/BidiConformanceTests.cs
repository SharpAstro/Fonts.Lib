using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SharpAstro.Fonts.Shaping;
using Shouldly;
using Xunit;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Conformance for <see cref="BidiAlgorithm"/> against the Unicode BidiCharacterTest.txt fixture:
/// each line gives input codepoints, a paragraph direction (0=LTR, 1=RTL, 2=auto), the expected
/// resolved paragraph level, the resolved per-character levels ('x' = removed by rule X9), and the
/// expected L2 visual ordering (logical indices, removed characters omitted). We check all three.
///
/// The committed fixture (Fixtures/BidiCharacterTest.txt) is a representative subset; point the
/// BIDI_TEST environment variable at the full Unicode file to run the complete ~96k-case suite.
/// </summary>
public class BidiConformanceTests
{
    private static string? FindFixture()
    {
        var env = Environment.GetEnvironmentVariable("BIDI_TEST");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var local = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BidiCharacterTest.txt");
        return File.Exists(local) ? local : null;
    }

    [Fact]
    public void BidiCharacterTest_Conformance()
    {
        var file = FindFixture();
        if (file is null)
        {
            Assert.Skip("BidiCharacterTest.txt fixture not available");
            return;
        }

        int total = 0, failures = 0, lineNo = 0;
        var report = new StringBuilder();

        foreach (var raw in File.ReadLines(file))
        {
            lineNo++;
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            var f = line.Split(';');
            if (f.Length < 5) continue;

            var cpText = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cps = new uint[cpText.Length];
            for (var i = 0; i < cpText.Length; i++)
                cps[i] = uint.Parse(cpText[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            var dir = int.Parse(f[1], CultureInfo.InvariantCulture);
            var expParaLevel = byte.Parse(f[2], CultureInfo.InvariantCulture);
            var expLevels = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expOrder = f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var paraInput = dir == 2 ? BidiAlgorithm.AutoLevel : dir;
            var levels = new byte[cps.Length];
            var paraLevel = BidiAlgorithm.Resolve(cps, paraInput, levels);

            total++;
            var ok = paraLevel == expParaLevel;

            if (ok)
                for (var i = 0; i < cps.Length; i++)
                    if (expLevels[i] != "x" && levels[i] != byte.Parse(expLevels[i], CultureInfo.InvariantCulture))
                    {
                        ok = false;
                        break;
                    }

            if (ok)
            {
                var visual = new int[cps.Length];
                BidiAlgorithm.Reorder(levels, visual);
                var got = new List<int>(cps.Length);
                foreach (var idx in visual)
                    if (expLevels[idx] != "x")
                        got.Add(idx);

                if (got.Count != expOrder.Length)
                    ok = false;
                else
                    for (var i = 0; i < got.Count; i++)
                        if (got[i] != int.Parse(expOrder[i], CultureInfo.InvariantCulture))
                        {
                            ok = false;
                            break;
                        }
            }

            if (!ok)
            {
                failures++;
                if (failures <= 25)
                    report.Append(CultureInfo.InvariantCulture,
                        $"L{lineNo}: {line}\n   got para={paraLevel} levels=[{string.Join(' ', levels)}]\n");
            }
        }

        failures.ShouldBe(0, $"{failures}/{total} BidiCharacterTest cases failed:\n{report}");
    }
}
