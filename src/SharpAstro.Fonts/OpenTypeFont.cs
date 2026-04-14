using SharpAstro.Fonts.Color;
using SharpAstro.Fonts.Hinting;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Tables.Cff;
using SharpAstro.Fonts.Tables.Cmap;
using SharpAstro.Fonts.Tables.Avar;
using SharpAstro.Fonts.Tables.Cbdt;
using SharpAstro.Fonts.Tables.Cblc;
using SharpAstro.Fonts.Tables.Colr;
using SharpAstro.Fonts.Tables.Cpal;
using SharpAstro.Fonts.Tables.Fvar;
using SharpAstro.Fonts.Tables.Gvar;
using SharpAstro.Fonts.Variations;
using SharpAstro.Fonts.Tables.Glyf;
using SharpAstro.Fonts.Tables.Head;
using SharpAstro.Fonts.Tables.Hhea;
using SharpAstro.Fonts.Tables.Hmtx;
using SharpAstro.Fonts.Tables.Gpos;
using SharpAstro.Fonts.Tables.Hvar;
using SharpAstro.Fonts.Tables.Mvar;
using SharpAstro.Fonts.Tables.Vvar;
using SharpAstro.Fonts.Tables.Cvar;
using SharpAstro.Fonts.Tables.Vhea;
using SharpAstro.Fonts.Tables.Vmtx;
using SharpAstro.Fonts.Tables.Kern;
using GposTable = SharpAstro.Fonts.Tables.Gpos.GposTable;
using HvarTable = SharpAstro.Fonts.Tables.Hvar.HvarTable;
using KernTable = SharpAstro.Fonts.Tables.Kern.KernTable;
using MvarTable = SharpAstro.Fonts.Tables.Mvar.MvarTable;
using SharpAstro.Fonts.Tables.Loca;
using SharpAstro.Fonts.Tables.Maxp;
using SharpAstro.Fonts.Tables.Sfnt;

namespace SharpAstro.Fonts;

/// <summary>
/// A loaded OpenType / TrueType font face.
///
/// <para><b>Thread-safety:</b> instances are immutable after construction —
/// every field references either an immutable record or a read-only view over
/// the original byte buffer. There are no internal mutexes, locks, or lazy
/// caches; concurrent reads from any number of threads are safe and
/// lock-free.</para>
///
/// <para><b>Memory:</b> the original font byte array is retained as
/// <see cref="ReadOnlyMemory{Byte}"/>; outline tables (glyf/loca) are parsed
/// on demand per <see cref="LoadGlyphOutline"/> call and not cached. Callers
/// that need repeated access should cache the returned <see cref="Outline"/>
/// themselves.</para>
/// </summary>
public sealed class OpenTypeFont
{
    public SfntDirectory Directory { get; }
    public HeadTable Head { get; }
    public MaxpTable Maxp { get; }
    public CmapTable Cmap { get; }
    public HheaTable? Hhea { get; }
    public HmtxTable? Hmtx { get; }
    public VheaTable? Vhea { get; }
    public VmtxTable? Vmtx { get; }
    public LocaTable? Loca { get; }
    public GlyfTable? Glyf { get; }
    internal CffTable? Cff { get; }
    public ColrTable? Colr { get; }
    public CpalTable? Cpal { get; }
    public CblcTable? Cblc { get; }
    public CbdtTable? Cbdt { get; }
    public FvarTable? Fvar { get; }
    public AvarTable? Avar { get; }
    public GvarTable? Gvar { get; }
    internal HvarTable? Hvar { get; }
    internal MvarTable? Mvar { get; }
    internal VvarTable? Vvar { get; }
    /// <summary>'cvar' CVT Variations — applied by the hinting pipeline when a
    /// variable font instance deviates from the default variation.</summary>
    internal CvarTable? Cvar { get; }
    internal KernTable? Kern { get; }
    internal GposTable? Gpos { get; }

    /// <summary>Normalized axis coordinates for the current variation instance.
    /// Empty for non-variable fonts; all-zeros for the default instance.</summary>
    internal ReadOnlySpan<float> NormalizedCoords => _normalizedCoords;

    /// <summary>'cvt ' Control Value Table (FUnit values used by hinting).</summary>
    internal ushort[]? CvtFunits { get; }
    /// <summary>'fpgm' Font Program — runs once at face load.</summary>
    internal byte[]? Fpgm { get; }
    /// <summary>'prep' CVT Program — runs each size change.</summary>
    internal byte[]? Prep { get; }

    /// <summary>True if this font ships hinting bytecode (cvt/fpgm/prep present).</summary>
    public bool HasHinting => Fpgm is not null || Prep is not null;

    /// <summary>Per-(ppem) cache of post-fpgm+prep interpreter snapshots.
    /// Lock-free; concurrent readers get a consistent snapshot per ppem.</summary>
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<float, Hinting.HintingSnapshot>
        HintingSnapshots = new();

    /// <summary>True if this font carries COLR + CPAL color glyph data.</summary>
    public bool HasColorGlyphs => Colr is not null && Cpal is not null;

    /// <summary>True if this font carries CBDT/CBLC color bitmap glyphs.</summary>
    public bool HasColorBitmaps => Cbdt is not null && Cblc is not null;

    /// <summary>True if this font is variable (has an 'fvar' table).</summary>
    public bool IsVariable => Fvar is not null;

    /// <summary>
    /// Currently-active normalized axis coordinates (length = fvar.Axes.Length,
    /// each in [-1, 1]). All zero = the font's default instance. Empty for
    /// non-variable fonts.
    /// </summary>
    private readonly float[] _normalizedCoords;

    private readonly CmapSubtable? _preferredCmap;

    private OpenTypeFont(SfntDirectory directory,
        HeadTable head, MaxpTable maxp, CmapTable cmap,
        HheaTable? hhea, HmtxTable? hmtx, LocaTable? loca, GlyfTable? glyf,
        CffTable? cff, ColrTable? colr, CpalTable? cpal,
        CblcTable? cblc, CbdtTable? cbdt,
        FvarTable? fvar, AvarTable? avar, GvarTable? gvar,
        HvarTable? hvar, MvarTable? mvar, VvarTable? vvar, CvarTable? cvar,
        KernTable? kern, GposTable? gpos,
        ushort[]? cvtFunits, byte[]? fpgm, byte[]? prep,
        float[] normalizedCoords,
        VheaTable? vhea, VmtxTable? vmtx)
    {
        Directory = directory;
        Head = head;
        Maxp = maxp;
        Cmap = cmap;
        Hhea = hhea;
        Hmtx = hmtx;
        Vhea = vhea;
        Vmtx = vmtx;
        Loca = loca;
        Glyf = glyf;
        Cff = cff;
        Colr = colr;
        Cpal = cpal;
        Cblc = cblc;
        Cbdt = cbdt;
        Fvar = fvar;
        Avar = avar;
        Gvar = gvar;
        Hvar = hvar;
        Mvar = mvar;
        Vvar = vvar;
        Cvar = cvar;
        Kern = kern;
        Gpos = gpos;
        CvtFunits = cvtFunits;
        Fpgm = fpgm;
        Prep = prep;
        _normalizedCoords = normalizedCoords;
        _preferredCmap = cmap.PreferredUnicodeSubtable();
    }

    /// <summary>
    /// Build a fresh hinting interpreter for this face. fpgm is executed
    /// immediately so user code only needs to call <see cref="Interpreter.OnSizeChange"/>
    /// before per-glyph runs. Returns null for fonts without hinting tables.
    ///
    /// <para><b>Phase 8 status:</b> the interpreter foundation runs but most
    /// hinting opcodes are no-ops — output is currently equivalent to the
    /// unhinted path. See ROADMAP.md / TODO.md.</para>
    /// </summary>
    internal Interpreter? CreateHintingInterpreter()
    {
        if (!HasHinting) return null;
        var interp = new Interpreter(
            Maxp.MaxStackElements, Maxp.MaxStorage,
            Maxp.MaxFunctionDefs, Maxp.MaxTwilightPoints,
            CvtFunits ?? []);
        interp.RunFpgm(Fpgm ?? []);
        return interp;
    }

    /// <summary>
    /// Return a new <see cref="OpenTypeFont"/> instance with the variation
    /// axes set to <paramref name="userCoords"/> (axis tag → user-space value;
    /// e.g. <c>{ "wght": 700, "wdth": 100 }</c>). Unspecified axes keep their
    /// defaults. Throws on a non-variable font. The returned instance is
    /// fully immutable and shares the parsed tables with <c>this</c>.
    /// </summary>
    public OpenTypeFont WithVariation(IReadOnlyDictionary<string, float> userCoords)
    {
        if (Fvar is null)
            throw new InvalidOperationException("Font has no 'fvar' table — not a variable font.");

        var norm = new float[Fvar.Axes.Length];
        for (var i = 0; i < Fvar.Axes.Length; i++)
        {
            var axis = Fvar.Axes[i];
            var u = axis.Default;
            if (userCoords.TryGetValue(axis.Tag.ToString(), out var supplied))
                u = supplied;
            norm[i] = axis.Normalize(u);
        }
        Avar?.Apply(norm);

        return new OpenTypeFont(Directory, Head, Maxp, Cmap, Hhea, Hmtx, Loca, Glyf,
            Cff, Colr, Cpal, Cblc, Cbdt, Fvar, Avar, Gvar, Hvar, Mvar, Vvar, Cvar,
            Kern, Gpos, CvtFunits, Fpgm, Prep, norm, Vhea, Vmtx);
    }

    /// <summary>True when the active variation is non-default (any axis ≠ 0 normalized).</summary>
    public bool IsVariationActive
    {
        get
        {
            foreach (var c in _normalizedCoords)
                if (c != 0f) return true;
            return false;
        }
    }

    /// <summary>True if this font uses CFF/CFF2 outlines (rather than TrueType glyf).</summary>
    public bool HasCffOutlines => Cff is not null;

    public ushort NumGlyphs => Maxp.NumGlyphs;
    public ushort UnitsPerEm => Head.UnitsPerEm;

    /// <summary>
    /// Look up a glyph id for a Unicode codepoint via the preferred Unicode
    /// cmap subtable. Returns 0 (.notdef) if not mapped.
    /// </summary>
    public uint GetGlyphId(uint codepoint)
        => _preferredCmap?.GetGlyphId(codepoint) ?? 0u;

    /// <summary>
    /// Look up a glyph id for a PDF char-code using the strategy in
    /// <paramref name="hint"/>. PDF embedded subset fonts often need
    /// non-Unicode lookup paths; see <see cref="GlyphMapHint"/>.
    /// </summary>
    public uint GetGlyphId(uint codepoint, uint charCode, GlyphMapHint hint)
        => Cmap.GetGlyphIdHinted(codepoint, charCode, hint, NumGlyphs);

    /// <summary>
    /// Decode a TrueType outline. Throws if this font is CFF-flavored — use
    /// <see cref="DrawGlyph"/> or <see cref="RenderGlyph"/> for format-agnostic
    /// rendering. Returns <see cref="Outline.Empty"/> for glyphs with no
    /// outline (e.g. space).
    /// </summary>
    public Outline LoadGlyphOutline(uint glyphId)
    {
        if (Glyf is null)
            throw new NotSupportedException(
                "This font has no 'glyf' table — use DrawGlyph(uint, IGlyphSink) for CFF fonts.");
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(glyphId, (uint)NumGlyphs);

        // For composite glyphs with variation, apply gvar component-anchor offset
        // deltas *before* assembling the composite, because they adjust per-component
        // translations rather than assembled outline points.
        if (Gvar is not null && IsVariationActive && Glyf.IsComposite(glyphId))
        {
            var componentCount = Glyf.GetComponentCount(glyphId);
            var deltas = CompositeVariation.GetComponentDeltas(
                Gvar, glyphId, _normalizedCoords, componentCount);
            // No further OutlineVariation.Apply: composite glyphs have no outline-point
            // deltas in gvar, only component-anchor deltas applied above.
            return Glyf.LoadGlyphWithVariation(glyphId, deltas);
        }

        var baseOutline = Glyf.LoadGlyph(glyphId);
        if (Gvar is not null && IsVariationActive)
            return OutlineVariation.Apply(baseOutline, Gvar, glyphId, _normalizedCoords);
        return baseOutline;
    }

    /// <summary>
    /// Format-agnostic outline emission. Walks either the 'glyf' (TrueType)
    /// or 'CFF' (Type 2) data for <paramref name="glyphId"/> and emits path
    /// commands to <paramref name="sink"/>. Allocation-free past the sink's
    /// own bookkeeping.
    /// </summary>
    public void DrawGlyph(uint glyphId, IGlyphSink sink)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(glyphId, (uint)NumGlyphs);

        if (Cff is not null)
        {
            Cff.DrawGlyph(glyphId, sink);
            return;
        }
        if (Glyf is not null)
        {
            // Use LoadGlyphOutline (not Glyf.LoadGlyph directly) so gvar
            // deltas are applied when a variation is active.
            var outline = LoadGlyphOutline(glyphId);
            if (!outline.IsEmpty) BezierFlattener.Walk(outline, sink);
            return;
        }
        throw new NotSupportedException("Font has neither 'glyf' nor 'CFF ' outline data.");
    }

    /// <summary>
    /// Get the kerning value (in FUnits) for the glyph pair. Prefers GPOS pair
    /// adjustment (lookup type 2) when present; falls back to the legacy 'kern'
    /// table otherwise. Returns 0 if no kerning pair exists or the font has no
    /// kerning data.
    /// </summary>
    public int GetKerning(uint leftGlyphId, uint rightGlyphId)
    {
        if (Gpos is not null)
        {
            var gposAdj = Gpos.GetPairAdjustment(leftGlyphId, rightGlyphId);
            if (gposAdj != 0) return gposAdj;
        }
        return Kern?.GetKerning(leftGlyphId, rightGlyphId) ?? 0;
    }

    /// <summary>
    /// Rasterize a glyph to an 8-bit grayscale alpha bitmap at
    /// <paramref name="pixelsPerEm"/>. Works for both TrueType and CFF.
    /// </summary>
    public GlyphBitmap RenderGlyph(uint glyphId, float pixelsPerEm,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
        => SmoothRasterizer.Rasterize(
            sink => DrawGlyph(glyphId, sink),
            pixelsPerEm, UnitsPerEm, subSamples);

    /// <summary>
    /// Load and hint a TrueType glyph outline at <paramref name="pixelsPerEm"/>.
    /// Returns null if the font has no hinting tables or no 'glyf' (use the
    /// unhinted <see cref="LoadGlyphOutline"/> path instead). For glyphs without
    /// a per-glyph instruction stream the returned outline is unhinted but
    /// still scaled to pixel coordinates.
    ///
    /// <para><b>Phase 8 status:</b> the interpreter currently implements only a
    /// subset of hinting opcodes — output may differ slightly from FreeType's
    /// hinted result until the remaining verbs land.</para>
    /// </summary>
    public HintedOutline? LoadHintedOutline(uint glyphId, float pixelsPerEm)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(glyphId, (uint)NumGlyphs);
        return HintingPipeline.Run(this, glyphId, pixelsPerEm);
    }

    /// <summary>
    /// Rasterize a hinted TrueType glyph. Falls back to <see cref="RenderGlyph"/>
    /// when the font has no hinting tables, when the glyph is CFF-flavored, or
    /// when the outline is empty.
    /// </summary>
    public GlyphBitmap RenderGlyphHinted(uint glyphId, float pixelsPerEm,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
    {
        var hinted = LoadHintedOutline(glyphId, pixelsPerEm);
        if (hinted is null || hinted.IsEmpty)
            return RenderGlyph(glyphId, pixelsPerEm, subSamples);

        // Hinted coords are already in pixel units; pass identity scale to
        // the rasterizer.
        return SmoothRasterizer.Rasterize(hinted.Walk, 1f, 1, subSamples);
    }

    /// <summary>
    /// Render a color glyph to an RGBA bitmap. Tries COLR v0/v1 first
    /// (vector + paint tree), then falls back to CBDT (PNG bitmap strikes).
    /// Returns null if this font / glyph has no color data — caller should
    /// fall back to <see cref="RenderGlyph"/>.
    /// </summary>
    public ColorBitmap? RenderColor(uint glyphId, float pixelsPerEm)
    {
        if (HasColorGlyphs)
        {
            var colr = ColrRenderer.TryRender(this, glyphId, pixelsPerEm);
            if (colr is not null) return colr;
        }
        if (HasColorBitmaps)
            return CbdtRenderer.TryRender(this, glyphId, pixelsPerEm);
        return null;
    }

    /// <summary>
    /// Load a font from raw SFNT bytes. The byte array is wrapped as
    /// <see cref="ReadOnlyMemory{Byte}"/> and retained — do not mutate it
    /// after passing in.
    /// </summary>
    public static OpenTypeFont Load(byte[] data, int faceOffset = 0)
        => Load(new ReadOnlyMemory<byte>(data), faceOffset);

    public static OpenTypeFont Load(ReadOnlyMemory<byte> data, int faceOffset = 0)
    {
        var span = data.Span;
        var dir = SfntDirectory.Parse(span, faceOffset);

        if (!dir.TryGet(Tags.Head, out var headRec))
            throw new InvalidDataException("Missing required 'head' table.");
        if (!dir.TryGet(Tags.Maxp, out var maxpRec))
            throw new InvalidDataException("Missing required 'maxp' table.");
        if (!dir.TryGet(Tags.Cmap, out var cmapRec))
            throw new InvalidDataException("Missing required 'cmap' table.");

        var head = HeadTable.Parse(headRec.Slice(span));
        var maxp = MaxpTable.Parse(maxpRec.Slice(span));
        var cmap = CmapTable.Parse(cmapRec.Slice(span));

        HheaTable? hhea = null;
        HmtxTable? hmtx = null;
        if (dir.TryGet(Tags.Hhea, out var hheaRec))
        {
            hhea = HheaTable.Parse(hheaRec.Slice(span));
            if (dir.TryGet(Tags.Hmtx, out var hmtxRec))
                hmtx = HmtxTable.Parse(hmtxRec.Slice(span), hhea.NumberOfHMetrics, maxp.NumGlyphs);
        }

        LocaTable? loca = null;
        GlyfTable? glyf = null;
        if (dir.TryGet(Tags.Loca, out var locaRec)
            && dir.TryGet(Tags.Glyf, out var glyfRec))
        {
            loca = LocaTable.Parse(locaRec.Slice(span), head.IndexToLocFormat, maxp.NumGlyphs);
            // GlyfTable holds a ReadOnlyMemory slice so it can be parsed lazily
            // without re-resolving the table directory on every glyph lookup.
            glyf = new GlyfTable(data.Slice((int)glyfRec.Offset, (int)glyfRec.Length), loca);
        }

        CffTable? cff = null;
        if (dir.TryGet(Tags.Cff, out var cffRec))
        {
            cff = CffTable.Parse(data.Slice((int)cffRec.Offset, (int)cffRec.Length),
                maxp.NumGlyphs, isCff2: false);
        }

        ColrTable? colr = null;
        CpalTable? cpal = null;
        if (dir.TryGet(Tags.Colr, out var colrRec))
            colr = ColrTable.Parse(data.Slice((int)colrRec.Offset, (int)colrRec.Length));
        if (dir.TryGet(Tags.Cpal, out var cpalRec))
            cpal = CpalTable.Parse(cpalRec.Slice(span));

        CblcTable? cblc = null;
        CbdtTable? cbdt = null;
        if (dir.TryGet(Tags.Cblc2, out var cblcRec))
            cblc = CblcTable.Parse(cblcRec.Slice(span));
        if (dir.TryGet(Tags.Cbdt2, out var cbdtRec))
            cbdt = new CbdtTable(data.Slice((int)cbdtRec.Offset, (int)cbdtRec.Length));

        FvarTable? fvar = null;
        AvarTable? avar = null;
        GvarTable? gvar = null;
        if (dir.TryGet(Tags.Fvar2, out var fvarRec))
            fvar = FvarTable.Parse(fvarRec.Slice(span));
        if (dir.TryGet(Tags.Avar2, out var avarRec))
            avar = AvarTable.Parse(avarRec.Slice(span));
        if (dir.TryGet(Tags.Gvar2, out var gvarRec))
            gvar = GvarTable.Parse(data.Slice((int)gvarRec.Offset, (int)gvarRec.Length));
        HvarTable? hvar = null;
        if (dir.TryGet(Tags.Hvar, out var hvarRec))
            hvar = HvarTable.Parse(hvarRec.Slice(span));

        MvarTable? mvar = null;
        if (dir.TryGet(Tags.Mvar, out var mvarRec))
            mvar = MvarTable.Parse(mvarRec.Slice(span));

        VvarTable? vvar = null;
        if (dir.TryGet(Tags.Vvar, out var vvarRec))
            vvar = VvarTable.Parse(vvarRec.Slice(span));

        // 'cvar' requires knowing the axis count; only parse when fvar was found.
        CvarTable? cvar = null;
        if (fvar is not null && dir.TryGet(Tags.Cvar, out var cvarRec))
            cvar = CvarTable.Parse(cvarRec.Slice(span), (ushort)fvar.Axes.Length);

        KernTable? kern = null;
        if (dir.TryGet(Tags.Kern, out var kernRec))
            kern = KernTable.Parse(kernRec.Slice(span));

        GposTable? gpos = null;
        if (dir.TryGet(Tags.Gpos, out var gposRec))
            gpos = GposTable.Parse(gposRec.Slice(span));

        VheaTable? vhea = null;
        VmtxTable? vmtx = null;
        if (dir.TryGet(Tags.Vhea, out var vheaRec))
        {
            vhea = VheaTable.Parse(vheaRec.Slice(span));
            if (dir.TryGet(Tags.Vmtx, out var vmtxRec))
                vmtx = VmtxTable.Parse(vmtxRec.Slice(span), vhea.NumberOfVMetrics, maxp.NumGlyphs);
        }

        var normCoords = fvar is not null ? new float[fvar.Axes.Length] : Array.Empty<float>();

        ushort[]? cvtFunits = null;
        byte[]? fpgm = null;
        byte[]? prep = null;
        if (dir.TryGet(Tags.Cvt2, out var cvtRec))
        {
            var cvtBytes = cvtRec.Slice(span);
            cvtFunits = new ushort[cvtBytes.Length / 2];
            for (var i = 0; i < cvtFunits.Length; i++)
                cvtFunits[i] = (ushort)((cvtBytes[i * 2] << 8) | cvtBytes[i * 2 + 1]);
        }
        if (dir.TryGet(Tags.Fpgm2, out var fpgmRec)) fpgm = fpgmRec.Slice(span).ToArray();
        if (dir.TryGet(Tags.Prep2, out var prepRec)) prep = prepRec.Slice(span).ToArray();

        return new OpenTypeFont(dir, head, maxp, cmap, hhea, hmtx, loca, glyf,
            cff, colr, cpal, cblc, cbdt, fvar, avar, gvar, hvar, mvar, vvar, cvar,
            kern, gpos, cvtFunits, fpgm, prep, normCoords, vhea, vmtx);
    }

    /// <summary>Convenience: load from a file path.</summary>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));
}
