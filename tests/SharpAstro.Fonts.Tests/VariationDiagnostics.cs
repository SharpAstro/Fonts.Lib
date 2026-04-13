using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tests;

public class VariationDiagnostics
{
    [Fact]
    public void Diagnose_RobotoFlex_WeightAxisDeltas()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.RobotoFlex));
        var sb = new System.Text.StringBuilder();

        // Dump axis info
        sb.AppendLine($"Axes: {font.Fvar!.Axes.Length}");
        foreach (var ax in font.Fvar.Axes)
            sb.AppendLine($"  {ax.Tag}: min={ax.Min} default={ax.Default} max={ax.Max}");

        var wghtAxis = font.Fvar.Axes[font.Fvar.FindAxisIndex(Tag.Parse("wght"))];
        sb.AppendLine($"\nWeight axis: min={wghtAxis.Min}, max={wghtAxis.Max}");

        var light = font.WithVariation(new Dictionary<string, float> { ["wght"] = wghtAxis.Min });
        var heavy = font.WithVariation(new Dictionary<string, float> { ["wght"] = wghtAxis.Max });

        var gid = font.GetGlyphId('B');
        sb.AppendLine($"\nGID for 'B' = {gid}");

        var baseO = font.LoadGlyphOutline(gid);
        var lightO = light.LoadGlyphOutline(gid);
        var heavyO = heavy.LoadGlyphOutline(gid);

        sb.AppendLine($"\nbase   bbox: x={baseO.Bounds.XMin}..{baseO.Bounds.XMax} y={baseO.Bounds.YMin}..{baseO.Bounds.YMax}");
        sb.AppendLine($"light  bbox: x={lightO.Bounds.XMin}..{lightO.Bounds.XMax} y={lightO.Bounds.YMin}..{lightO.Bounds.YMax}");
        sb.AppendLine($"heavy  bbox: x={heavyO.Bounds.XMin}..{heavyO.Bounds.XMax} y={heavyO.Bounds.YMin}..{heavyO.Bounds.YMax}");

        // Compute max coord delta
        var maxDx = 0;
        var maxDy = 0;
        var diffPoints = 0;
        for (var i = 0; i < baseO.PointCount; i++)
        {
            var dx = Math.Abs(heavyO.X[i] - lightO.X[i]);
            var dy = Math.Abs(heavyO.Y[i] - lightO.Y[i]);
            if (dx > maxDx) maxDx = dx;
            if (dy > maxDy) maxDy = dy;
            if (dx > 0 || dy > 0) diffPoints++;
        }
        sb.AppendLine($"\nLight vs Heavy: maxDx={maxDx}, maxDy={maxDy}, diffPoints={diffPoints}/{baseO.PointCount}");

        // Test gvar.LoadGlyphTuples directly
        if (font.Gvar is not null)
        {
            sb.AppendLine($"\nGvar.HasDataForGlyph({gid}) = {font.Gvar.HasDataForGlyph(gid)}");
            var tuples = font.Gvar.LoadGlyphTuples(gid, baseO.PointCount);
            sb.AppendLine($"Tuple count for gid {gid}: {tuples.Count}");
            for (var i = 0; i < Math.Min(5, tuples.Count); i++)
            {
                var t = tuples[i];
                var peakStr = string.Join(",", t.Peak.Select(p => p.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
                int maxDeltaX = 0, maxDeltaY = 0;
                for (var k = 0; k < t.DeltaX.Length; k++)
                {
                    if (Math.Abs(t.DeltaX[k]) > maxDeltaX) maxDeltaX = Math.Abs(t.DeltaX[k]);
                    if (Math.Abs(t.DeltaY[k]) > maxDeltaY) maxDeltaY = Math.Abs(t.DeltaY[k]);
                }
                sb.AppendLine($"  tuple[{i}]: peak=[{peakStr}], pts={(t.PointNumbers?.Length.ToString() ?? "all")}, deltaCount={t.DeltaX.Length}, maxDx={maxDeltaX}, maxDy={maxDeltaY}");
            }

            // Print active normalized coords
            var heavyCoords = new System.Text.StringBuilder();
            for (var i = 0; i < font.Fvar!.Axes.Length; i++)
                heavyCoords.Append($"{font.Fvar.Axes[i].Tag}={(i < tuples.Count && tuples.Count > 0 ? "?" : "?")},");
            sb.AppendLine($"\n(heavy normalized coords inspect via reflection — skipping)");

            // Manually compute scalar for the 'wght' tuple at heavy
            sb.AppendLine($"\nManually testing scalar at heavy weight:");
            var wghtIdx = font.Fvar.FindAxisIndex(Tag.Parse("wght"));
            var heavyNorm = new float[font.Fvar.Axes.Length];
            heavyNorm[wghtIdx] = 1.0f; // max → +1
            for (var i = 0; i < Math.Min(5, tuples.Count); i++)
            {
                var s = tuples[i].ComputeScalar(heavyNorm);
                sb.AppendLine($"  tuple[{i}] scalar at heavy(wght=1.0) = {s:F4}");
            }
        }

        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "var_diag.txt"), sb.ToString());
    }
}
