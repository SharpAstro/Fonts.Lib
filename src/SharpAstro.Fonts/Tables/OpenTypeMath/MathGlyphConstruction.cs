using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// Per-glyph stretching recipe: a chain of pre-drawn larger variants and
/// (optionally) an assembly recipe for sizes beyond the largest variant.
///
/// <para>Typical consumer flow for "I need this radical/paren at height H":</para>
/// <list type="number">
/// <item>Walk <see cref="Variants"/>, pick the smallest whose
/// <see cref="MathGlyphVariant.AdvanceMeasurement"/> ≥ H, render that.</item>
/// <item>If none fit and <see cref="Assembly"/> is non-null, stack assembly
/// parts (with extenders repeated) to reach H. See <see cref="MathGlyphPart"/>
/// for the connector-overlap rules.</item>
/// <item>If no variants and no assembly, fall back to the base glyph at its
/// natural size.</item>
/// </list>
///
/// <para>The first entry in <see cref="Variants"/> is the base (unstretched)
/// glyph; subsequent entries are progressively larger.</para>
/// </summary>
public sealed class MathGlyphConstruction
{
    public IReadOnlyList<MathGlyphVariant> Variants { get; }
    public MathGlyphAssembly? Assembly { get; }

    private MathGlyphConstruction(MathGlyphVariant[] variants, MathGlyphAssembly? assembly)
    {
        Variants = variants;
        Assembly = assembly;
    }

    /// <summary>
    /// Parse a MathGlyphConstruction subtable. <paramref name="data"/> starts
    /// at the subtable's own offset (NOT at the parent MathVariants table).
    /// </summary>
    internal static MathGlyphConstruction Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var assemblyOffset = r.ReadUInt16();
        var variantCount = r.ReadUInt16();
        var variants = new MathGlyphVariant[variantCount];
        for (var i = 0; i < variantCount; i++)
        {
            var glyphId = r.ReadUInt16();
            var advance = r.ReadUInt16();
            variants[i] = new MathGlyphVariant(glyphId, advance);
        }

        MathGlyphAssembly? assembly = null;
        if (assemblyOffset != 0 && assemblyOffset < data.Length)
        {
            assembly = MathGlyphAssembly.Parse(data[assemblyOffset..]);
        }
        return new MathGlyphConstruction(variants, assembly);
    }
}
