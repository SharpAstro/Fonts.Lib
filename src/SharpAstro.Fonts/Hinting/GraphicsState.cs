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

    /// <summary>Round state — controls how distances are quantized to pixel grid.</summary>
    public byte RoundState;
    public int RoundPeriod;     // F26.6
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

    public static GraphicsState Default => new()
    {
        AutoFlip = true,
        RoundState = 1, // RTG (round to grid)
        RoundPeriod = F26Dot6.One,
        RoundPhase = 0,
        RoundThreshold = F26Dot6.One / 2,
        ControlValueCutIn = 17 << 6 / 16, // 17/16 px = 1.0625 (default)
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
    };
}
