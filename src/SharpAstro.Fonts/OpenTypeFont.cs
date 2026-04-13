using SharpAstro.Fonts.Color;
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
        float[] normalizedCoords)
    {
        Directory = directory;
        Head = head;
        Maxp = maxp;
        Cmap = cmap;
        Hhea = hhea;
        Hmtx = hmtx;
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
        _normalizedCoords = normalizedCoords;
        _preferredCmap = cmap.PreferredUnicodeSubtable();
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
            Cff, Colr, Cpal, Cblc, Cbdt, Fvar, Avar, Gvar, norm);
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
    /// Rasterize a glyph to an 8-bit grayscale alpha bitmap at
    /// <paramref name="pixelsPerEm"/>. Works for both TrueType and CFF.
    /// </summary>
    public GlyphBitmap RenderGlyph(uint glyphId, float pixelsPerEm,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
        => SmoothRasterizer.Rasterize(
            sink => DrawGlyph(glyphId, sink),
            pixelsPerEm, UnitsPerEm, subSamples);

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
        var normCoords = fvar is not null ? new float[fvar.Axes.Length] : Array.Empty<float>();

        return new OpenTypeFont(dir, head, maxp, cmap, hhea, hmtx, loca, glyf,
            cff, colr, cpal, cblc, cbdt, fvar, avar, gvar, normCoords);
    }

    /// <summary>Convenience: load from a file path.</summary>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));
}
