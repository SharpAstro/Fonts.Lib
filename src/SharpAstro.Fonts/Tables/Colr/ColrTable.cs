using System.Collections.Frozen;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Tables.Hvar;
using SharpAstro.Fonts.Variations;

namespace SharpAstro.Fonts.Tables.Colr;

/// <summary>
/// One v0 layer record.
/// </summary>
public readonly record struct ColrV0Layer(ushort GlyphId, ushort PaletteIndex);

/// <summary>
/// Parsed 'COLR' table — supports both v0 (layered) and v1 (paint tree).
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/colr</para>
///
/// <para>v0 lookups produce an array of layer records (each = outline glyph
/// + palette color). v1 lookups produce a <see cref="PaintRef"/> root that
/// the renderer walks recursively.</para>
/// </summary>
public sealed class ColrTable
{
    public ushort Version { get; }
    public bool HasV1 => Version >= 1;

    /// <summary>v0 base-glyph index keyed by GID → start of layer range.</summary>
    private readonly FrozenDictionary<ushort, (ushort First, ushort Num)> _v0Index;
    private readonly ColrV0Layer[] _v0Layers;

    /// <summary>v1 base-glyph index keyed by GID → root paint offset (relative to BaseGlyphList).</summary>
    private readonly FrozenDictionary<ushort, uint> _v1Index;

    /// <summary>Slice of the COLR table starting at BaseGlyphList — paint offsets are relative to this.</summary>
    private readonly ReadOnlyMemory<byte> _v1Source;

    /// <summary>v1 LayerList (for PaintColrLayers): array of paint offsets (uint32 each, relative to LayerList start).</summary>
    private readonly uint[] _v1LayerOffsets;
    private readonly ReadOnlyMemory<byte> _v1LayerListSource;

    /// <summary>
    /// Item Variation Store embedded in the COLR table (present on COLR v1
    /// variable fonts). Used to evaluate Var* paint format deltas.
    /// Null when the table carries no variation data.
    /// </summary>
    internal ItemVariationStore? VariationStore { get; }

    /// <summary>
    /// DeltaSetIndexMap for the COLR VarIndexMap field. Maps a sequential
    /// per-paint var-index to an (outer, inner) IVS lookup pair.
    /// Null when absent (var-indices are used directly as outer=0, inner=idx).
    /// </summary>
    internal DeltaSetIndexMap? VarIndexMap { get; }

    private ColrTable(ushort version,
        FrozenDictionary<ushort, (ushort, ushort)> v0Index, ColrV0Layer[] v0Layers,
        FrozenDictionary<ushort, uint> v1Index, ReadOnlyMemory<byte> v1Source,
        uint[] v1LayerOffsets, ReadOnlyMemory<byte> v1LayerListSource,
        ItemVariationStore? variationStore, DeltaSetIndexMap? varIndexMap)
    {
        Version = version;
        _v0Index = v0Index;
        _v0Layers = v0Layers;
        _v1Index = v1Index;
        _v1Source = v1Source;
        _v1LayerOffsets = v1LayerOffsets;
        _v1LayerListSource = v1LayerListSource;
        VariationStore = variationStore;
        VarIndexMap = varIndexMap;
    }

    /// <summary>
    /// Look up a variation delta for a packed varIndex (outer&lt;&lt;16|inner as
    /// stored in the paint record, or a sequential index through VarIndexMap)
    /// against the embedded IVS. Returns 0 when no IVS is present or when
    /// <paramref name="normalizedCoords"/> is empty (default instance).
    /// </summary>
    internal float GetVarDelta(uint varIndex, ReadOnlySpan<float> normalizedCoords)
    {
        if (VariationStore is null || normalizedCoords.IsEmpty) return 0f;
        int outer, inner;
        if (VarIndexMap is not null)
            (outer, inner) = VarIndexMap.Map(varIndex);
        else
        {
            // No map: the varIndex encodes outer (high 16 bits) + inner (low 16 bits).
            outer = (int)(varIndex >> 16);
            inner = (int)(varIndex & 0xFFFF);
        }
        return VariationStore.GetDelta(outer, inner, normalizedCoords);
    }

    /// <summary>v0 / fallback: returns the layer slice for <paramref name="gid"/>, or empty span.</summary>
    public ReadOnlySpan<ColrV0Layer> GetV0Layers(uint gid)
    {
        if (gid > ushort.MaxValue) return [];
        if (!_v0Index.TryGetValue((ushort)gid, out var range)) return [];
        return new ReadOnlySpan<ColrV0Layer>(_v0Layers, range.First, range.Num);
    }

    /// <summary>v1: returns the root paint for <paramref name="gid"/> if any.</summary>
    public bool TryGetV1RootPaint(uint gid, out PaintRef paint)
    {
        paint = default;
        if (!HasV1 || gid > ushort.MaxValue) return false;
        if (!_v1Index.TryGetValue((ushort)gid, out var off)) return false;
        paint = new PaintRef(_v1Source, (int)off);
        return true;
    }

    /// <summary>v1: get the <c>i</c>-th layer paint of a <see cref="PaintFormat.ColrLayers"/> record.</summary>
    public PaintRef GetLayerPaint(int layerIndex)
    {
        if (_v1LayerOffsets.Length == 0
            || (uint)layerIndex >= (uint)_v1LayerOffsets.Length)
            return default;
        var off = _v1LayerOffsets[layerIndex];
        // LayerList paint offsets are relative to the LayerList start.
        return new PaintRef(_v1LayerListSource, (int)off);
    }

    public static ColrTable Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var r = new BigEndianReader(span);
        var version = r.ReadUInt16();
        var numBaseV0 = r.ReadUInt16();
        var baseV0Off = r.ReadUInt32();
        var layersV0Off = r.ReadUInt32();
        var numLayerRecordsV0 = r.ReadUInt16();

        // v0: BaseGlyphRecord[numBaseV0] = { gid(uint16), firstLayer(uint16), numLayers(uint16) }
        var v0IndexBuilder = new Dictionary<ushort, (ushort, ushort)>(numBaseV0);
        var br = new BigEndianReader(span, (int)baseV0Off);
        for (var i = 0; i < numBaseV0; i++)
        {
            var gid = br.ReadUInt16();
            var first = br.ReadUInt16();
            var num = br.ReadUInt16();
            v0IndexBuilder[gid] = (first, num);
        }
        // v0: LayerRecord[numLayerRecordsV0] = { gid(uint16), paletteIdx(uint16) }
        var v0Layers = new ColrV0Layer[numLayerRecordsV0];
        var lr = new BigEndianReader(span, (int)layersV0Off);
        for (var i = 0; i < numLayerRecordsV0; i++)
        {
            var gid = lr.ReadUInt16();
            var pal = lr.ReadUInt16();
            v0Layers[i] = new ColrV0Layer(gid, pal);
        }

        var v1IndexBuilder = new Dictionary<ushort, uint>();
        ReadOnlyMemory<byte> v1Source = ReadOnlyMemory<byte>.Empty;
        var v1LayerOffsets = Array.Empty<uint>();
        ReadOnlyMemory<byte> v1LayerListSource = ReadOnlyMemory<byte>.Empty;
        ItemVariationStore? variationStore = null;
        DeltaSetIndexMap? varIndexMap = null;

        if (version >= 1)
        {
            var baseGlyphListOff = r.ReadUInt32();
            var layerListOff = r.ReadUInt32();
            // clipListOff (uint32) + varIndexMapOff (uint32) + itemVariationStoreOff (uint32).
            var clipListOff = r.ReadUInt32();       // reserved — not used for rendering
            var varIndexMapOff = r.ReadUInt32();
            var itemVariationStoreOff = r.ReadUInt32();
            _ = clipListOff;

            if (baseGlyphListOff != 0)
            {
                v1Source = data[(int)baseGlyphListOff..];
                var bglR = new BigEndianReader(v1Source.Span);
                var n = bglR.ReadUInt32();
                for (uint i = 0; i < n; i++)
                {
                    var gid = bglR.ReadUInt16();
                    var paintOff = bglR.ReadUInt32();
                    v1IndexBuilder[gid] = paintOff;
                }
            }

            if (layerListOff != 0)
            {
                v1LayerListSource = data[(int)layerListOff..];
                var llR = new BigEndianReader(v1LayerListSource.Span);
                var n = llR.ReadUInt32();
                v1LayerOffsets = new uint[n];
                for (uint i = 0; i < n; i++)
                    v1LayerOffsets[i] = llR.ReadUInt32();
            }

            if (itemVariationStoreOff != 0)
                variationStore = ItemVariationStore.Parse(span[(int)itemVariationStoreOff..]);

            if (varIndexMapOff != 0)
                varIndexMap = DeltaSetIndexMap.Parse(span[(int)varIndexMapOff..]);
        }

        return new ColrTable(version,
            v0IndexBuilder.ToFrozenDictionary(), v0Layers,
            v1IndexBuilder.ToFrozenDictionary(), v1Source,
            v1LayerOffsets, v1LayerListSource,
            variationStore, varIndexMap);
    }
}
