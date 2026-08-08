using System.Runtime.InteropServices;
using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// Glue between <see cref="OpenTypeFont.LoadGlyphOutline"/> (which produces
/// design-unit outlines) and the bytecode <see cref="Interpreter"/> (which
/// operates on F26.6 pixel-scaled <see cref="Zone"/>s).
///
/// <para>Post-fpgm+prep state is cached per (font, ppem) in
/// <see cref="OpenTypeFont.HintingSnapshots"/>. Each glyph render clones
/// the snapshot into a per-call <see cref="Interpreter"/> — no fpgm/prep
/// re-execution, and fully thread-safe (concurrent glyphs at the same ppem
/// share the snapshot, each with an independent mutable clone).</para>
/// </summary>
internal static class HintingPipeline
{
    /// <summary>
    /// Load <paramref name="glyphId"/>, scale to <paramref name="ppem"/>, and
    /// run the glyph's hint program (if any). Returns null when the font has
    /// no hinting tables; returns an unhinted-but-scaled outline when the
    /// font has hinting tables but the glyph itself carries no instructions.
    /// </summary>
    public static HintedOutline? Run(OpenTypeFont font, uint glyphId, float ppem)
    {
        if (font.Glyf is null) return null;
        if (!font.HasHinting) return null;
        if (ppem <= 0f) return HintedOutline.Empty;

        // Already known not to terminate at this size — skip straight to the unhinted path
        // instead of burning the budget again.
        if (font.HintingBudgetFailures.ContainsKey((glyphId, ppem))) return null;

        var outline = font.LoadGlyphOutline(glyphId);
        if (outline.IsEmpty) return HintedOutline.Empty;

        var snap = GetOrCreateSnapshot(font, ppem);
        if (snap is null) return null;
        var interp = new Interpreter(snap);

        var n = outline.PointCount;
        var zone = new Zone(n + 4);
        zone.PointCount = n + 4;

        var srcX = outline.X;
        var srcY = outline.Y;
        var srcFlags = outline.Flags;
        for (var i = 0; i < n; i++)
        {
            var px = interp.ScaleFunitsToPx(srcX[i]);
            var py = interp.ScaleFunitsToPx(srcY[i]);
            zone.OrgX[i] = px;
            zone.CurX[i] = px;
            zone.OrgY[i] = py;
            zone.CurY[i] = py;
            zone.Flags[i] = (byte)(srcFlags[i] & Zone.FlagOnCurve);
        }

        // Phantom points (FT convention):
        //   pp1 = (xMin - lsb, 0)              → horizontal left origin
        //   pp2 = (pp1.x + advanceWidth, 0)    → horizontal right origin
        //   pp3 = (0, top side bearing)        → vertical top (defaults 0)
        //   pp4 = (0, pp3.y - advanceHeight)   → vertical bottom (defaults 0)
        var advanceWidth = font.Hmtx?.GetAdvanceWidth(glyphId) ?? 0;
        var lsb = font.Hmtx?.GetLeftSideBearing(glyphId) ?? 0;
        var pp1xFu = outline.Bounds.XMin - lsb;
        var pp2xFu = pp1xFu + advanceWidth;
        zone.OrgX[n + 0] = zone.CurX[n + 0] = interp.ScaleFunitsToPx(pp1xFu);
        zone.OrgX[n + 1] = zone.CurX[n + 1] = interp.ScaleFunitsToPx(pp2xFu);
        // pp3 = (0, topSideBearing) in Y; pp4 = (0, pp3.y - advanceHeight) in Y.
        var pp3yFu = font.Vmtx?.GetTopSideBearing(glyphId) ?? (short)0;
        var pp4yFu = pp3yFu - (font.Vmtx?.GetAdvanceHeight(glyphId) ?? 0);
        zone.OrgY[n + 2] = zone.CurY[n + 2] = interp.ScaleFunitsToPx(pp3yFu);
        zone.OrgY[n + 3] = zone.CurY[n + 3] = interp.ScaleFunitsToPx(pp4yFu);

        // Compute once — reused for both the interpreter and the output.
        // Zero-copy unwrap: the ImmutableArray and the backing int[] share
        // the same reference; safe because the interpreter only reads it.
        var ends = ImmutableCollectionsMarshal.AsArray(outline.ContourEndsImmutable)!;

        var instructions = outline.Instructions;
        if (instructions is { Length: > 0 })
        {
            interp.SetGlyphContours(ends);
            interp.ResetInstructionBudget();
            try
            {
                interp.RunGlyphProgram(instructions, zone);
            }
            catch (HintingBudgetExceededException)
            {
                // Non-terminating hint program. Give up on hinting this glyph rather than
                // wedging the caller; null makes RenderGlyphHinted fall back to RenderGlyph.
                // Remember the verdict so the next render of this glyph is free.
                font.HintingBudgetFailures[(glyphId, ppem)] = true;
                return null;
            }
            finally
            {
                interp.SetGlyphContours(null);
            }
        }

        // Strip phantom points and copy back the visible contour points.
        var hx = new int[n];
        var hy = new int[n];
        var hf = new byte[n];
        Array.Copy(zone.CurX, hx, n);
        Array.Copy(zone.CurY, hy, n);
        for (var i = 0; i < n; i++)
            hf[i] = (byte)(zone.Flags[i] & Zone.FlagOnCurve);

        return new HintedOutline(hx, hy, hf, ends);
    }

    /// <summary>Return a cached post-fpgm+prep snapshot for <paramref name="ppem"/>,
    /// or build one (thread-safe via ConcurrentDictionary).</summary>
    private static HintingSnapshot? GetOrCreateSnapshot(OpenTypeFont font, float ppem)
    {
        if (font.HintingSnapshots.TryGetValue(ppem, out var cached))
            return cached;

        HintingSnapshot snap;
        try
        {
            // fpgm runs inside CreateHintingInterpreter, so it is covered by this guard too.
            var interp = font.CreateHintingInterpreter();
            if (interp is null) return null;

            interp.ResetInstructionBudget();
            interp.OnSizeChange(ppem, font.UnitsPerEm, font.Prep ?? []);
            snap = interp.TakeSnapshot();
        }
        catch (HintingBudgetExceededException)
        {
            // A non-terminating prep (or fpgm, executed at interpreter construction) poisons every
            // glyph at this size, not just one — so disable hinting for the whole face rather than
            // re-running the same doomed program on the next glyph.
            return null;
        }

        // Race-safe: if another thread built one concurrently, either is fine.
        return font.HintingSnapshots.GetOrAdd(ppem, snap);
    }
}
