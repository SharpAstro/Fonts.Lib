using SharpAstro.Fonts.Color;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Tables.Cff;
using SharpAstro.Fonts.Tables.Cmap;
using SharpAstro.Fonts.Tables.Colr;
using SharpAstro.Fonts.Tables.Cpal;
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

    /// <summary>True if this font carries COLR + CPAL color glyph data.</summary>
    public bool HasColorGlyphs => Colr is not null && Cpal is not null;

    private readonly CmapSubtable? _preferredCmap;

    private OpenTypeFont(SfntDirectory directory,
        HeadTable head, MaxpTable maxp, CmapTable cmap,
        HheaTable? hhea, HmtxTable? hmtx, LocaTable? loca, GlyfTable? glyf,
        CffTable? cff, ColrTable? colr, CpalTable? cpal)
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
        _preferredCmap = cmap.PreferredUnicodeSubtable();
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
        if (glyphId >= NumGlyphs)
            throw new ArgumentOutOfRangeException(nameof(glyphId),
                $"glyphId {glyphId} >= numGlyphs {NumGlyphs}");
        return Glyf.LoadGlyph(glyphId);
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
            var outline = Glyf.LoadGlyph(glyphId);
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
    /// Render a color glyph (COLR v0 / v1) to an RGBA bitmap. Returns null
    /// if this font / glyph has no color data — the caller should fall back
    /// to <see cref="RenderGlyph"/> + colorize.
    /// </summary>
    public ColorBitmap? RenderColor(uint glyphId, float pixelsPerEm)
        => HasColorGlyphs ? ColrRenderer.TryRender(this, glyphId, pixelsPerEm) : null;

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

        return new OpenTypeFont(dir, head, maxp, cmap, hhea, hmtx, loca, glyf, cff, colr, cpal);
    }

    /// <summary>Convenience: load from a file path.</summary>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));
}
