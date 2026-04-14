namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// TrueType interpreter graphics state — the "registers" of the TT VM.
/// Subset of the spec's full state limited to what v40-grayscale mode
/// uses (drops sub-pixel rendering control, scan converter type
/// override, etc.).
/// </summary>
internal struct GraphicsState
{
    /// <summary>Auto-flip behavior when measuring negative distances.</summary>
    public bool AutoFlip;

    /// <summary>Round mode — selects the rounding routine. See <see cref="Hinting.RoundMode"/>.</summary>
    public RoundMode RoundMode;
    public int RoundPeriod;     // F26.6 — used by ROUND_SUPER / SUPER45
    public int RoundPhase;      // F26.6
    public int RoundThreshold;  // F26.6

    /// <summary>Control value table cut-in (F26.6) — minimum distance for using CVT value.</summary>
    public int ControlValueCutIn;

    /// <summary>Minimum distance (F26.6) for MDRP/MIRP movements.</summary>
    public int MinimumDistance;

    /// <summary>Single-width cut-in / value (F26.6).</summary>
    public int SingleWidthCutIn;
    public int SingleWidthValue;

    /// <summary>delta_base / delta_shift used by DELTAP/DELTAC instructions.</summary>
    public ushort DeltaBase;
    public ushort DeltaShift;

    /// <summary>Reference points (indices into the active zone's point array).</summary>
    public int Rp0, Rp1, Rp2;

    /// <summary>Zone pointers (0 = twilight, 1 = glyph).</summary>
    public byte Zp0, Zp1, Zp2;

    /// <summary>Loop counter — set by SLOOP, decremented by looped instructions.</summary>
    public int Loop;

    /// <summary>Projection / freedom / dual-projection vectors (F2.14, x and y components).</summary>
    public short ProjX, ProjY;
    public short FreeX, FreeY;
    public short DualX, DualY;

    /// <summary>Instruction control flags (set by INSTCTRL).</summary>
    public byte InstructControl;

    /// <summary>Scan control flags.</summary>
    public ushort ScanControl;
    public byte ScanType;

    /// <summary>
    /// Engine compensation (F26.6) per distance type — bits 0-1 of MDRP/MIRP
    /// opcodes encode the "color": 0=black, 1=white, 2=gray, 3=reserved.
    /// FreeType adds the matching entry to the distance before rounding.
    /// In v40 / grayscale mode FT uses {0,0,0,0}; non-zero values matter
    /// for v35 / native ClearType compatibility.
    /// </summary>
    public int CompensationBlack;
    public int CompensationWhite;
    public int CompensationGray;
    public int CompensationReserved;

    public static GraphicsState Default => new()
    {
        AutoFlip = true,
        RoundMode = RoundMode.Grid, // RTG default per spec
        RoundPeriod = F26Dot6.One,
        RoundPhase = 0,
        RoundThreshold = F26Dot6.One / 2,
        // 17/16 px = 1.0625 px in F26.6 = 68. Parens required — without them,
        // C# precedence parses this as `17 << (6/16)` = `17 << 0` = 17, which
        // makes virtually every CVT cut-in fail.
        ControlValueCutIn = (17 << 6) / 16,
        MinimumDistance = F26Dot6.One,
        SingleWidthCutIn = 0,
        SingleWidthValue = 0,
        DeltaBase = 9,
        DeltaShift = 3,
        Rp0 = 0, Rp1 = 0, Rp2 = 0,
        Zp0 = 1, Zp1 = 1, Zp2 = 1,
        Loop = 1,
        ProjX = 0x4000, ProjY = 0,    // X-axis (1.0 in F2.14)
        FreeX = 0x4000, FreeY = 0,
        DualX = 0x4000, DualY = 0,
        ScanControl = 0,
        ScanType = 2,
        // v40 / grayscale: all compensations zero (per FT tt_metrics defaults).
        CompensationBlack = 0,
        CompensationWhite = 0,
        CompensationGray = 0,
        CompensationReserved = 0,
    };

    /// <summary>Look up engine compensation by the 2-bit color field.</summary>
    public int CompensationFor(int color) => (color & 3) switch
    {
        0 => CompensationBlack,
        1 => CompensationWhite,
        2 => CompensationGray,
        _ => CompensationReserved,
    };
}
