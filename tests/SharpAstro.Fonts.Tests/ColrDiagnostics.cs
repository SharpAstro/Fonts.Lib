using SharpAstro.Fonts.Tables.Colr;

namespace SharpAstro.Fonts.Tests;

public class ColrDiagnostics
{
    [Fact]
    public void Diagnostic_BrightEmoji_PixelStats()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var sb = new System.Text.StringBuilder();
        // Known bright emoji to spot-check the renderer.
        int[] codepoints = [
            0x1F534, // 🔴 RED CIRCLE
            0x1F7E2, // 🟢 GREEN CIRCLE
            0x1F7E1, // 🟡 YELLOW CIRCLE
            0x2600,  // ☀ SUN
            0x1F525, // 🔥 FIRE
            0x1F600, // 😀 GRINNING FACE
        ];
        foreach (var cp in codepoints)
        {
            var gid = font.GetGlyphId((uint)cp);
            sb.AppendLine($"U+{cp:X5} → gid={gid}");
            if (gid == 0) continue;
            var bmp = font.RenderColor(gid, 96f);
            if (bmp is null || bmp.IsEmpty) { sb.AppendLine("  null/empty"); continue; }
            int rSum = 0, gSum = 0, bSum = 0, opaqueCount = 0;
            for (var i = 0; i < bmp.Pixels.Length; i += 4)
            {
                if (bmp.Pixels[i + 3] > 0)
                {
                    rSum += bmp.Pixels[i];
                    gSum += bmp.Pixels[i + 1];
                    bSum += bmp.Pixels[i + 2];
                    opaqueCount++;
                }
            }
            if (opaqueCount > 0)
                sb.AppendLine($"  size={bmp.Width}x{bmp.Height}, opaque={opaqueCount}, avgRGB=({rSum/opaqueCount},{gSum/opaqueCount},{bSum/opaqueCount})");
            // Save PNG
            var pngDir = System.IO.Path.Combine(AppContext.BaseDirectory, "PngDumps");
            Directory.CreateDirectory(pngDir);
            PngWriter.WriteRgba(System.IO.Path.Combine(pngDir, $"diag_U+{cp:X5}.png"),
                bmp.Pixels, bmp.Width, bmp.Height);
        }
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "bright_emoji_stats.txt"), sb.ToString());
    }

    [Fact]
    public void Diagnostic_PixelStatsForGid13()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var bmp = font.RenderColor(13, 96f);
        var sb = new System.Text.StringBuilder();
        if (bmp is null) { sb.AppendLine("null"); }
        else
        {
            sb.AppendLine($"size={bmp.Width}x{bmp.Height}, left={bmp.Left}, top={bmp.Top}");
            int a0 = 0, aMid = 0, a255 = 0;
            int rSum = 0, gSum = 0, bSum = 0, opaqueCount = 0;
            for (var i = 0; i < bmp.Pixels.Length; i += 4)
            {
                var a = bmp.Pixels[i + 3];
                if (a == 0) a0++;
                else if (a == 255) a255++;
                else aMid++;
                if (a > 0)
                {
                    rSum += bmp.Pixels[i];
                    gSum += bmp.Pixels[i + 1];
                    bSum += bmp.Pixels[i + 2];
                    opaqueCount++;
                }
            }
            sb.AppendLine($"alpha=0: {a0}, alpha 1..254: {aMid}, alpha=255: {a255}");
            if (opaqueCount > 0)
                sb.AppendLine($"avg opaque RGB: ({rSum / opaqueCount}, {gSum / opaqueCount}, {bSum / opaqueCount})");
        }
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "pixel_stats.txt"), sb.ToString());
    }

    [Fact]
    public void Diagnostic_DumpNotoCOLRv1_FirstColorGlyph()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.Noto_COLRv1));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"NumGlyphs={font.NumGlyphs}, UnitsPerEm={font.UnitsPerEm}");
        sb.AppendLine($"COLR.HasV1={font.Colr!.HasV1}");
        sb.AppendLine($"CPAL.NumPalettes={font.Cpal!.NumPalettes}, NumEntries={font.Cpal.NumPaletteEntries}");

        // Dump first 5 palette entries
        var pal = font.Cpal.GetPalette(0);
        for (var i = 0; i < Math.Min(5, pal.Length); i++)
            sb.AppendLine($"  pal[{i}] = R={pal[i].R} G={pal[i].G} B={pal[i].B} A={pal[i].A}");

        // Find first GID that has a v1 root paint
        for (uint gid = 0; gid < font.NumGlyphs; gid++)
        {
            if (!font.Colr.TryGetV1RootPaint(gid, out var root)) continue;
            sb.AppendLine($"\nGID {gid}: root paint format = {root.Format} @ offset {root.Offset}");
            DumpPaint(sb, font, root, depth: 1);
            break;
        }

        // Write to file for inspection
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "colr_diag.txt");
        File.WriteAllText(path, sb.ToString());
        // Always pass — diagnostic only.
    }

    private static void DumpPaint(System.Text.StringBuilder sb, OpenTypeFont font, PaintRef p, int depth)
    {
        if (p.IsNull || depth > 6)
        {
            sb.AppendLine($"{Indent(depth)}<null or depth limit>");
            return;
        }
        sb.Append(Indent(depth));
        switch (p.Format)
        {
            case PaintFormat.ColrLayers:
            {
                var d = p.AsColrLayers();
                sb.AppendLine($"ColrLayers: num={d.NumLayers}, first={d.FirstLayerIndex}");
                for (var i = 0; i < d.NumLayers; i++)
                    DumpPaint(sb, font, font.Colr!.GetLayerPaint((int)(d.FirstLayerIndex + i)), depth + 1);
                break;
            }
            case PaintFormat.Solid:
            {
                var d = p.AsSolid();
                sb.AppendLine($"Solid: pal={d.PaletteIndex}, alpha={d.Alpha:F4}");
                break;
            }
            case PaintFormat.Glyph:
            {
                var d = p.AsGlyph();
                sb.AppendLine($"Glyph: glyphId={d.GlyphID}");
                DumpPaint(sb, font, d.Paint, depth + 1);
                break;
            }
            case PaintFormat.LinearGradient:
            {
                var d = p.AsLinearGradient(default!);
                sb.AppendLine($"LinearGradient: extend={d.Extend}, stops={d.Stops.Length}, " +
                    $"p0=({d.X0},{d.Y0}) p1=({d.X1},{d.Y1}) p2=({d.X2},{d.Y2})");
                foreach (var s in d.Stops)
                    sb.AppendLine($"{Indent(depth + 1)}stop @ {s.StopOffset:F3}: pal={s.PaletteIndex}, alpha={s.Alpha:F3}");
                break;
            }
            case PaintFormat.RadialGradient:
            {
                var d = p.AsRadialGradient(default!);
                sb.AppendLine($"RadialGradient: extend={d.Extend}, stops={d.Stops.Length}, " +
                    $"c0=({d.X0},{d.Y0},r={d.R0}) c1=({d.X1},{d.Y1},r={d.R1})");
                foreach (var s in d.Stops)
                    sb.AppendLine($"{Indent(depth + 1)}stop @ {s.StopOffset:F3}: pal={s.PaletteIndex}, alpha={s.Alpha:F3}");
                break;
            }
            case PaintFormat.Transform:
            {
                var d = p.AsTransform();
                sb.AppendLine($"Transform: M={d.Transform}");
                DumpPaint(sb, font, d.Paint, depth + 1);
                break;
            }
            default:
                sb.AppendLine($"{p.Format} (not dumped)");
                break;
        }
    }

    private static string Indent(int depth) => new(' ', depth * 2);
}
