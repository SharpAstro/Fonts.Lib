namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// TrueType rounding mode set by RTG/RTHG/RTDG/RUTG/RDTG/ROFF/SROUND/S45ROUND.
/// Names follow FreeType's <c>TT_Round_*</c> conventions.
/// </summary>
internal enum RoundMode : byte
{
    /// <summary>Round to half-integer pixel boundary (RTHG, 0x19).</summary>
    HalfGrid = 0,
    /// <summary>Round to nearest pixel (RTG, 0x18). Default per spec.</summary>
    Grid = 1,
    /// <summary>Round to nearest half-pixel (RTDG, 0x3D).</summary>
    DoubleGrid = 2,
    /// <summary>Round down to next pixel (RDTG, 0x7D).</summary>
    DownToGrid = 3,
    /// <summary>Round up to next pixel (RUTG, 0x7C).</summary>
    UpToGrid = 4,
    /// <summary>No rounding (ROFF, 0x7A).</summary>
    Off = 5,
    /// <summary>Configurable rounding from SROUND (0x76).</summary>
    Super = 6,
    /// <summary>Configurable 45° rounding from S45ROUND (0x77).</summary>
    Super45 = 7,
}

/// <summary>
/// Distance quantization per the active <see cref="RoundMode"/>. All distances
/// are F26.6 (1/64 px). Algorithms ported from FreeType <c>ttinterp.c</c>
/// (<c>Round_*</c> functions) but rewritten — the FT source informs the
/// algorithm only.
/// </summary>
internal static class Rounding
{
    public static int Round(int distance, in GraphicsState gs) => gs.RoundMode switch
    {
        RoundMode.Off        => distance,
        RoundMode.Grid       => RoundToGrid(distance),
        RoundMode.HalfGrid   => RoundToHalfGrid(distance),
        RoundMode.DoubleGrid => RoundToDoubleGrid(distance),
        RoundMode.DownToGrid => RoundDownToGrid(distance),
        RoundMode.UpToGrid   => RoundUpToGrid(distance),
        RoundMode.Super      => RoundSuper(distance, gs.RoundPeriod, gs.RoundPhase, gs.RoundThreshold),
        RoundMode.Super45    => RoundSuper(distance, gs.RoundPeriod, gs.RoundPhase, gs.RoundThreshold),
        _ => distance,
    };

    // Known limitation: FreeType adds an "engine compensation" distance bias
    // (per render mode) before rounding MDRP/MIRP distances. We don't — the bias
    // is ~0 in grayscale / v40 mode (our only path), so it's immaterial here;
    // revisit only for B&W hinting conformance. See TODO.md "Hinting".

    public static int RoundToGrid(int distance)
    {
        // ((d + 32) & -64) preserving sign; clamp to phase 0 to avoid the
        // distance "flipping past zero" after rounding.
        if (distance >= 0)
        {
            var v = (distance + 32) & ~63;
            return v < 0 ? 0 : v;
        }
        else
        {
            var v = -(((-distance) + 32) & ~63);
            return v > 0 ? 0 : v;
        }
    }

    public static int RoundToHalfGrid(int distance)
    {
        if (distance >= 0)
        {
            var v = ((distance) & ~63) + 32;
            return v < 0 ? 32 : v;
        }
        else
        {
            var v = -(((-distance) & ~63) + 32);
            return v > 0 ? -32 : v;
        }
    }

    public static int RoundToDoubleGrid(int distance)
    {
        if (distance >= 0)
        {
            var v = (distance + 16) & ~31;
            return v < 0 ? 0 : v;
        }
        else
        {
            var v = -(((-distance) + 16) & ~31);
            return v > 0 ? 0 : v;
        }
    }

    public static int RoundDownToGrid(int distance)
    {
        if (distance >= 0)
        {
            var v = distance & ~63;
            return v < 0 ? 0 : v;
        }
        else
        {
            var v = -((-distance) & ~63);
            return v > 0 ? 0 : v;
        }
    }

    public static int RoundUpToGrid(int distance)
    {
        if (distance >= 0)
        {
            var v = (distance + 63) & ~63;
            return v < 0 ? 0 : v;
        }
        else
        {
            var v = -(((-distance) + 63) & ~63);
            return v > 0 ? 0 : v;
        }
    }

    /// <summary>
    /// Generic configurable rounding — drives RTG/RTHG/RTDG/RDTG/RUTG via the
    /// (period, phase, threshold) tuple set on GraphicsState by SROUND/S45ROUND
    /// (or by the simple round-mode setters that pre-fill those fields).
    /// </summary>
    public static int RoundSuper(int distance, int period, int phase, int threshold)
    {
        if (period <= 0) period = 64;
        var mask = ~(period - 1); // assumes period is power-of-2; FT uses arbitrary period via div, but powers-of-2 cover SROUND's encoded values
        if (distance >= 0)
        {
            var v = (distance + (threshold - phase)) & mask;
            v += phase;
            if (v < 0) v = phase;
            return v;
        }
        else
        {
            var v = -(((threshold - phase) - distance) & mask);
            v -= phase;
            if (v > 0) v = -phase;
            return v;
        }
    }

    /// <summary>
    /// Decode the SROUND argument byte into (period, phase, threshold) in F26.6.
    /// Per OpenType spec.
    /// </summary>
    public static void DecodeSRoundArg(byte arg, bool super45,
        out int period, out int phase, out int threshold)
    {
        // Period: bits 7-6
        // 0 -> 1/2 px, 1 -> 1 px, 2 -> 2 px, 3 -> reserved
        // For S45ROUND, the unit is sqrt(2)/2 instead.
        var periodCode = (arg >> 6) & 0x3;
        var phaseCode  = (arg >> 4) & 0x3;
        var thresholdCode = arg & 0xF;

        if (super45)
        {
            // sqrt(2)/2 ≈ 0.7071 → in F26.6 = 45
            period = periodCode switch { 0 => 22, 1 => 45, 2 => 90, _ => 45 }; // round to int F26.6
        }
        else
        {
            period = periodCode switch { 0 => 32, 1 => 64, 2 => 128, _ => 64 };
        }
        phase = phaseCode switch
        {
            0 => 0,
            1 => period / 4,
            2 => period / 2,
            3 => 3 * period / 4,
            _ => 0,
        };
        // Threshold: 0 means period/2 - 1; otherwise (code - 4) * period / 8.
        if (thresholdCode == 0)
            threshold = period - 1;
        else
            threshold = ((thresholdCode - 4) * period) / 8;
    }
}
