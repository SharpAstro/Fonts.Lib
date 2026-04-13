using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Tables.Cmap;
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

    private readonly CmapSubtable? _preferredCmap;

    private OpenTypeFont(SfntDirectory directory,
        HeadTable head, MaxpTable maxp, CmapTable cmap,
        HheaTable? hhea, HmtxTable? hmtx, LocaTable? loca, GlyfTable? glyf)
    {
        Directory = directory;
        Head = head;
        Maxp = maxp;
        Cmap = cmap;
        Hhea = hhea;
        Hmtx = hmtx;
        Loca = loca;
        Glyf = glyf;
        _preferredCmap = cmap.PreferredUnicodeSubtable();
    }

    public ushort NumGlyphs => Maxp.NumGlyphs;
    public ushort UnitsPerEm => Head.UnitsPerEm;

    /// <summary>
    /// Look up a glyph id for a Unicode codepoint via the preferred Unicode
    /// cmap subtable. Returns 0 (.notdef) if not mapped.
    /// </summary>
    public uint GetGlyphId(uint codepoint)
        => _preferredCmap?.GetGlyphId(codepoint) ?? 0u;

    /// <summary>
    /// Decode a TrueType outline. Throws if this font is CFF-flavored (use the
    /// CFF loader once Phase 4 lands). Returns <see cref="Outline.Empty"/> for
    /// glyphs with no outline (e.g. space).
    /// </summary>
    public Outline LoadGlyphOutline(uint glyphId)
    {
        if (Glyf is null)
            throw new NotSupportedException(
                "This font has no 'glyf' table (likely a CFF font; CFF support lands in Phase 4).");
        if (glyphId >= NumGlyphs)
            throw new ArgumentOutOfRangeException(nameof(glyphId),
                $"glyphId {glyphId} >= numGlyphs {NumGlyphs}");
        return Glyf.LoadGlyph(glyphId);
    }

    /// <summary>
    /// Rasterize a glyph to an 8-bit grayscale alpha bitmap at
    /// <paramref name="pixelsPerEm"/>. Convenience wrapper over
    /// <see cref="LoadGlyphOutline"/> + <see cref="SmoothRasterizer"/>.
    /// </summary>
    public GlyphBitmap RenderGlyph(uint glyphId, float pixelsPerEm,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
    {
        var outline = LoadGlyphOutline(glyphId);
        if (outline.IsEmpty) return GlyphBitmap.Empty;
        return SmoothRasterizer.Rasterize(outline, pixelsPerEm, UnitsPerEm, subSamples);
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

        return new OpenTypeFont(dir, head, maxp, cmap, hhea, hmtx, loca, glyf);
    }

    /// <summary>Convenience: load from a file path.</summary>
    public static OpenTypeFont LoadFromFile(string path)
        => Load(File.ReadAllBytes(path));
}
