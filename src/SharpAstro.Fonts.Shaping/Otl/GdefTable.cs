using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// GDEF glyph classes (ClassDef values). <see cref="None"/> means the font's GDEF
/// doesn't classify the glyph (or there is no GDEF) — lookupFlag skipping then
/// treats it as unclassified, never skipped by class.
/// </summary>
internal enum GlyphClass : byte
{
    None = 0,
    Base = 1,
    Ligature = 2,
    Mark = 3,
    Component = 4,
}

/// <summary>
/// OpenType GDEF table — the slices lookup processing needs: GlyphClassDef
/// (base/ligature/mark/component), MarkAttachClassDef (for the
/// MarkAttachmentType lookupFlag filter), and MarkGlyphSets (for the
/// UseMarkFilteringSet flag). AttachList and LigCaretList (caret positions for
/// A4-era text editing) are not parsed here.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/gdef</para>
/// </summary>
internal sealed class GdefTable
{
    private readonly ClassDef _glyphClasses;
    private readonly ClassDef _markAttachClasses;
    private readonly Coverage[] _markGlyphSets;

    private GdefTable(ClassDef glyphClasses, ClassDef markAttachClasses, Coverage[] markGlyphSets)
    {
        _glyphClasses = glyphClasses;
        _markAttachClasses = markAttachClasses;
        _markGlyphSets = markGlyphSets;
    }

    /// <summary>A GDEF with no data — all glyphs unclassified. Lets the shaper hold a
    /// non-null GdefTable for fonts without GDEF (common in PDF subset fonts).</summary>
    public static readonly GdefTable Empty = new(ClassDef.Empty, ClassDef.Empty, []);

    /// <summary>Whether the font carries a GlyphClassDef. When false, the shaper synthesizes
    /// mark/base classes from Unicode general category (HarfBuzz's fallback for fonts without
    /// GDEF glyph classes — common in subset/PDF fonts).</summary>
    public bool HasGlyphClasses => !ReferenceEquals(_glyphClasses, ClassDef.Empty);

    public GlyphClass GetGlyphClass(uint glyphId)
    {
        var cls = _glyphClasses.GetClass(glyphId);
        return cls is >= 1 and <= 4 ? (GlyphClass)cls : GlyphClass.None;
    }

    /// <summary>Mark-attachment class for the MarkAttachmentType lookupFlag filter (0 = unclassified).</summary>
    public int GetMarkAttachClass(uint glyphId) => _markAttachClasses.GetClass(glyphId);

    /// <summary>Whether <paramref name="glyphId"/> is in mark-filtering set <paramref name="setIndex"/>.
    /// An out-of-range set index matches nothing (the lookup then skips every mark).</summary>
    public bool IsInMarkGlyphSet(int setIndex, uint glyphId)
        => (uint)setIndex < (uint)_markGlyphSets.Length && _markGlyphSets[setIndex].Contains(glyphId);

    /// <summary>Parse GDEF. Returns <see cref="Empty"/> on malformed/unsupported data (non-fatal).</summary>
    public static GdefTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return Empty;
        var r = new BigEndianReader(data);
        var major = r.ReadUInt16();
        var minor = r.ReadUInt16();
        if (major != 1) return Empty;

        var glyphClassDefOffset = r.ReadUInt16();
        r.Skip(2); // attachListOffset — not used
        r.Skip(2); // ligCaretListOffset — not used (A4-era)
        var markAttachClassDefOffset = r.ReadUInt16();

        // Version 1.2 adds markGlyphSetsDefOffset; 1.3 adds itemVarStoreOffset (unused here).
        ushort markGlyphSetsDefOffset = 0;
        if (minor >= 2 && r.Remaining >= 2)
            markGlyphSetsDefOffset = r.ReadUInt16();

        var glyphClasses = ClassDef.Parse(data, glyphClassDefOffset);
        var markAttachClasses = ClassDef.Parse(data, markAttachClassDefOffset);

        var markSets = Array.Empty<Coverage>();
        if (markGlyphSetsDefOffset > 0 && markGlyphSetsDefOffset + 4 <= data.Length)
        {
            var ms = new BigEndianReader(data[markGlyphSetsDefOffset..]);
            var format = ms.ReadUInt16();
            if (format == 1)
            {
                var count = ms.ReadUInt16();
                if (ms.Remaining >= count * 4)
                {
                    markSets = new Coverage[count];
                    var setsBase = data[markGlyphSetsDefOffset..];
                    for (var i = 0; i < count; i++)
                    {
                        // MarkGlyphSets uses 32-bit offsets (unlike most OTL offsets).
                        var covOffset = (int)ms.ReadUInt32();
                        markSets[i] = Coverage.Parse(setsBase, covOffset);
                    }
                }
            }
        }

        return new GdefTable(glyphClasses, markAttachClasses, markSets);
    }
}
