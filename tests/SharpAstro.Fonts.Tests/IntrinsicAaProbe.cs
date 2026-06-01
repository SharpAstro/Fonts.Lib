using SharpAstro.Fonts.Rasterizer;
using Xunit;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// INTRINSIC anti-aliasing fidelity — no peer oracle (FreeType/pdfium), no "is a
/// difference bad?" ambiguity. The analytically-correct alpha of a glyph pixel is its
/// exact ink-area coverage; our SmoothRasterizer approximates that with exact X coverage
/// + N-way Y supersampling. Rendering at a very high N converges to truth, so the gap
/// between production (N=4) and high-N output is our rasterizer's ABSOLUTE AA error in
/// coverage units (0-255). Small error ⇒ our AA is faithful and any pdfium "difference"
/// is pdfium's choice (hinting/darkening) or irreducible, not our defect.
/// Diagnostic (always passes); read the output.
/// </summary>
public class IntrinsicAaProbe
{
    private readonly ITestOutputHelper _output;
    public IntrinsicAaProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Coverage_Convergence_4x_vs_64x()
    {
        const int truthSub = 64;
        var fonts = new[] { Fixtures.DejaVuSans, Fixtures.SourceSans3 };
        int[] cps = ['e', 'a', 'g', 'H', '8'];
        int[] ppems = [8, 10, 12, 16, 24];

        _output.WriteLine($"{"font",-16} {"ch",-3} {"ppem",4}  {"meanErr",8} {"maxErr",7} {"px>8",6}");
        foreach (var fontFile in fonts)
        {
            var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
            var shortName = System.IO.Path.GetFileNameWithoutExtension(fontFile);
            foreach (var cp in cps)
            {
                var gid = font.GetGlyphId((uint)cp);
                if (gid == 0) continue;
                foreach (var ppem in ppems)
                {
                    var prod = font.RenderGlyph(gid, ppem, SmoothRasterizer.DefaultSubSamples); // N=4
                    var truth = font.RenderGlyph(gid, ppem, truthSub);                          // N=64
                    if (prod.IsEmpty || truth.IsEmpty) continue;
                    // Same bbox (dims depend on outline extent, not subsample count).
                    if (prod.Width != truth.Width || prod.Height != truth.Height) { _output.WriteLine($"  dim mismatch {shortName} {(char)cp} {ppem}"); continue; }

                    long sum = 0; int max = 0, over8 = 0;
                    for (var i = 0; i < prod.Alpha.Length; i++)
                    {
                        var d = Math.Abs(prod.Alpha[i] - truth.Alpha[i]);
                        sum += d; if (d > max) max = d; if (d > 8) over8++;
                    }
                    var mean = (double)sum / prod.Alpha.Length;
                    _output.WriteLine($"{shortName,-16} {(char)cp,-3} {ppem,4}  {mean,8:F2} {max,7} {over8,6}");
                }
            }
        }
    }
}
