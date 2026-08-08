namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// One TrueType "zone" of points — either the glyph zone (zone 1, with the
/// glyph's outline + 4 phantom points) or the twilight zone (zone 0, scratch
/// storage initialized to 0).
///
/// <para>Coordinates are stored as F26.6 (1/64 pixel). Each zone keeps both
/// "current" and "original" copies — instructions that move points modify
/// only "current", and certain hinting ops compare against "original".</para>
/// </summary>
internal sealed class Zone
{
    /// <summary>Number of points in this zone (including 4 phantom points for zone 1).</summary>
    public int PointCount;

    /// <summary>Current X coordinates, F26.6.</summary>
    public int[] CurX;
    /// <summary>Current Y coordinates, F26.6.</summary>
    public int[] CurY;
    /// <summary>Original (pre-instruction) X, F26.6 — used by certain hinting ops.</summary>
    public int[] OrgX;
    /// <summary>Original Y, F26.6.</summary>
    public int[] OrgY;
    /// <summary>Per-point flags. Bit 0 = on-curve; bits 1-3 = touched X / Y / both.</summary>
    public byte[] Flags;

    public Zone(int capacity)
    {
        // Every allocated slot is a usable point. This matters for the twilight zone, which
        // nothing else ever sizes: the glyph zone gets its count set by HintingPipeline, but
        // zone 0 is only ever constructed, so leaving PointCount at 0 here made every
        // twilight bounds-check fail and silently turned the whole zone into a no-op.
        PointCount = capacity;
        CurX = new int[capacity];
        CurY = new int[capacity];
        OrgX = new int[capacity];
        OrgY = new int[capacity];
        Flags = new byte[capacity];
    }

    public const byte FlagOnCurve = 0x01;
    public const byte FlagTouchedX = 0x02;
    public const byte FlagTouchedY = 0x04;

    /// <summary>Deep copy — used by <see cref="HintingSnapshot"/> to clone the
    /// twilight zone for per-call interpreter instances.</summary>
    public Zone Clone() => new(CurX.Length)
    {
        PointCount = PointCount,
        CurX = (int[])CurX.Clone(),
        CurY = (int[])CurY.Clone(),
        OrgX = (int[])OrgX.Clone(),
        OrgY = (int[])OrgY.Clone(),
        Flags = (byte[])Flags.Clone(),
    };
}
