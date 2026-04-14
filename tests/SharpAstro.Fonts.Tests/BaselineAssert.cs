using System.Runtime.CompilerServices;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Regression baselines: byte-exact comparison of <see cref="GlyphBitmap"/>
/// alpha output against a committed <c>Baselines/{name}.bmp</c>.
///
/// <para>On mismatch: writes the actual + diff BMPs into <c>Actual/</c> and
/// <c>Diff/</c> under the test output directory and fails with a path
/// pointer.</para>
///
/// <para>If the baseline does not exist OR the env var
/// <c>BASELINE_REGEN=1</c> is set, the helper writes the current bitmap
/// straight into the SOURCE <c>Baselines/</c> folder (resolved via
/// <see cref="CallerFilePathAttribute"/>) and asserts inconclusive — the
/// developer eyeballs the new baseline, then re-runs to lock it in.</para>
/// </summary>
internal static class BaselineAssert
{
    private static readonly string OutDir = AppContext.BaseDirectory;
    private static readonly bool RegenAll =
        Environment.GetEnvironmentVariable("BASELINE_REGEN") == "1";

    public static void Matches(GlyphBitmap bmp, string name,
        [CallerFilePath] string callerFile = "")
    {
        if (bmp.IsEmpty)
            throw new InvalidOperationException(
                $"Refusing to baseline empty bitmap '{name}' — assertion likely wrong.");

        // Source-tree baseline path (committed alongside the tests).
        // On CI the CallerFilePath may resolve to an inaccessible root (e.g. /_/);
        // all reads/writes go through outBaselinePath in that case.
        var srcBaselineDir = Path.Combine(Path.GetDirectoryName(callerFile)!, "Baselines");
        var srcBaselinePath = Path.Combine(srcBaselineDir, name + ".bmp");

        // Output-tree mirror, populated by the csproj <Content> copy step.
        var outBaselinePath = Path.Combine(OutDir, "Baselines", name + ".bmp");

        var baselineExists = File.Exists(outBaselinePath) || File.Exists(srcBaselinePath);
        if (RegenAll || !baselineExists)
        {
            Directory.CreateDirectory(srcBaselineDir);
            BmpWriter.WriteGray8(srcBaselinePath, bmp.Alpha, bmp.Width, bmp.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(outBaselinePath)!);
            BmpWriter.WriteGray8(outBaselinePath, bmp.Alpha, bmp.Width, bmp.Height);
            throw new BaselineCreatedException(
                $"Baseline '{name}' did not exist — wrote {srcBaselinePath}. " +
                "Eyeball it, commit it, re-run.");
        }

        var (expected, exW, exH) = BmpReader.ReadGray8(
            File.Exists(outBaselinePath) ? outBaselinePath : srcBaselinePath);

        if (exW != bmp.Width || exH != bmp.Height)
        {
            DumpFailure(bmp, expected, exW, exH, name);
            throw new Xunit.Sdk.XunitException(
                $"Baseline size mismatch '{name}': expected {exW}x{exH}, got {bmp.Width}x{bmp.Height}. " +
                $"Actual + diff dumped under {OutDir}.");
        }

        if (!bmp.Alpha.AsSpan().SequenceEqual(expected))
        {
            DumpFailure(bmp, expected, exW, exH, name);
            throw new Xunit.Sdk.XunitException(
                $"Baseline pixel mismatch '{name}'. Actual + diff dumped under {OutDir}. " +
                $"If the change is intentional: copy {Path.Combine(OutDir, "Actual", name + ".bmp")} " +
                $"over {srcBaselinePath} (or set BASELINE_REGEN=1 and re-run).");
        }
    }

    private static void DumpFailure(GlyphBitmap actual, byte[] expected, int exW, int exH, string name)
    {
        var actDir = Path.Combine(OutDir, "Actual");
        var diffDir = Path.Combine(OutDir, "Diff");
        Directory.CreateDirectory(actDir);
        Directory.CreateDirectory(diffDir);

        BmpWriter.WriteGray8(Path.Combine(actDir, name + ".bmp"),
            actual.Alpha, actual.Width, actual.Height);

        if (exW == actual.Width && exH == actual.Height)
        {
            var diff = new byte[expected.Length];
            for (var i = 0; i < diff.Length; i++)
                diff[i] = (byte)Math.Abs(expected[i] - actual.Alpha[i]);
            BmpWriter.WriteGray8(Path.Combine(diffDir, name + ".bmp"), diff, exW, exH);
        }
    }

    /// <summary>
    /// Thrown when a baseline is freshly created. We surface it as a normal
    /// xUnit failure so the test reports "X of Y baselines new" — the dev
    /// reviews them and re-runs.
    /// </summary>
    public sealed class BaselineCreatedException(string message)
        : Xunit.Sdk.XunitException(message);
}
