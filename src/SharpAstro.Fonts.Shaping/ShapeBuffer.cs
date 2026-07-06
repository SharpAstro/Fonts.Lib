using System.Text;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>Direction a run is laid out in. The engine does not do bidi (UAX #9) —
/// callers pass pre-segmented single-direction runs, exactly like HarfBuzz.</summary>
public enum ShapeDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// The mutable glyph run a shaper works on: parallel arrays of glyph data, reusable
/// across calls (geometric growth, no steady-state allocation). Fill it with
/// <see cref="AddText"/>, hand it to <see cref="Shaper.Shape"/>, then read the results.
///
/// <para><b>Slots hold codepoints before shaping, glyph ids after</b> — shaping maps
/// codepoints through cmap in place (HarfBuzz's model). <see cref="Clusters"/> are
/// UTF-16 code-unit offsets into the source text (plus the <c>clusterOffset</c> passed
/// to <see cref="AddText"/>) — the same convention DIR.Lib's <c>ShapedGlyph.Cluster</c>
/// uses; a ligature merges clusters to the minimum (cluster level 0).</para>
///
/// <para><b>Positions are deltas in font units.</b> <see cref="XAdvanceDeltas"/> is the
/// GPOS/kern adjustment <em>relative to the glyph's hmtx advance</em> — deliberately not
/// the absolute advance, so renderer-side glyph caches stay authoritative for base
/// advances (the A2 renderer contract). <see cref="XOffsets"/>/<see cref="YOffsets"/>
/// are placement shifts (Y up, font units).</para>
///
/// <para>Not thread-safe; use one buffer per shaping thread.</para>
/// </summary>
public sealed class ShapeBuffer
{
    private uint[] _glyphs = new uint[64];
    private int[] _clusters = new int[64];
    private ushort[] _masks = new ushort[64];
    private byte[] _classes = new byte[64];
    private int[] _advDeltas = new int[64];
    private int[] _xOffsets = new int[64];
    private int[] _yOffsets = new int[64];
    private int _length;

    /// <summary>Number of glyphs currently in the buffer.</summary>
    public int Length => _length;

    /// <summary>Run direction. Set before shaping; RTL runs are reversed into visual
    /// order by <see cref="Shaper.Shape"/> (mirroring arrives with the H4 shapers).</summary>
    public ShapeDirection Direction { get; set; } = ShapeDirection.LeftToRight;

    /// <summary>Glyph ids after shaping (Unicode codepoints before — see class remarks).</summary>
    public ReadOnlySpan<uint> GlyphIds => _glyphs.AsSpan(0, _length);

    /// <summary>UTF-16 cluster offsets into the source text (see class remarks).</summary>
    public ReadOnlySpan<int> Clusters => _clusters.AsSpan(0, _length);

    /// <summary>X-advance adjustments in font units, relative to each glyph's hmtx advance.</summary>
    public ReadOnlySpan<int> XAdvanceDeltas => _advDeltas.AsSpan(0, _length);

    /// <summary>X placement offsets in font units.</summary>
    public ReadOnlySpan<int> XOffsets => _xOffsets.AsSpan(0, _length);

    /// <summary>Y placement offsets in font units (positive = up).</summary>
    public ReadOnlySpan<int> YOffsets => _yOffsets.AsSpan(0, _length);

    // Mutable views for the shaper/appliers (same assembly).
    internal Span<uint> GlyphsMutable => _glyphs.AsSpan(0, _length);
    internal Span<int> ClustersMutable => _clusters.AsSpan(0, _length);
    internal Span<ushort> MasksMutable => _masks.AsSpan(0, _length);
    internal Span<byte> ClassesMutable => _classes.AsSpan(0, _length);
    internal Span<int> AdvDeltasMutable => _advDeltas.AsSpan(0, _length);
    internal Span<int> XOffsetsMutable => _xOffsets.AsSpan(0, _length);
    internal Span<int> YOffsetsMutable => _yOffsets.AsSpan(0, _length);

    /// <summary>Reset to an empty buffer (keeps capacity). Direction is preserved.</summary>
    public void Clear() => _length = 0;

    /// <summary>
    /// Append one run of text as unshaped codepoints. Ill-formed UTF-16 yields U+FFFD
    /// (the <see cref="MemoryExtensions.EnumerateRunes(ReadOnlySpan{char})"/> convention —
    /// the same the pre-seam renderers used). <paramref name="clusterOffset"/> biases the
    /// recorded cluster values: pass the run's start offset within the full line so
    /// clusters index the line, not the run.
    /// </summary>
    public void AddText(ReadOnlySpan<char> text, int clusterOffset = 0)
    {
        var cluster = clusterOffset;
        foreach (var rune in text.EnumerateRunes())
        {
            EnsureCapacity(_length + 1);
            _glyphs[_length] = (uint)rune.Value;
            _clusters[_length] = cluster;
            _masks[_length] = ushort.MaxValue; // all features apply until a shaper assigns per-glyph masks (H4)
            _classes[_length] = 0;
            _advDeltas[_length] = 0;
            _xOffsets[_length] = 0;
            _yOffsets[_length] = 0;
            _length++;
            cluster += rune.Utf16SequenceLength;
        }
    }

    /// <summary>Replace the glyph id at <paramref name="index"/> in place (GSUB single substitution).</summary>
    internal void Substitute(int index, uint glyphId, GlyphClass glyphClass)
    {
        _glyphs[index] = glyphId;
        _classes[index] = (byte)glyphClass;
    }

    /// <summary>Add positioning deltas (font units) to the glyph at <paramref name="index"/> (GPOS).</summary>
    internal void AddPosition(int index, int xAdvance, int xOffset, int yOffset)
    {
        _advDeltas[index] += xAdvance;
        _xOffsets[index] += xOffset;
        _yOffsets[index] += yOffset;
    }

    /// <summary>
    /// Form a ligature (GSUB type 4). <paramref name="componentIndices"/> are the ascending
    /// buffer positions of the matched components (found skip-aware, so possibly
    /// non-contiguous); <paramref name="componentIndices"/>[0] receives the ligature glyph and
    /// the remaining component slots are removed. Glyphs <em>between</em> components that were
    /// skipped (marks) are preserved, shifting left to close the gaps. Clusters across the whole
    /// span [first..last] merge to their minimum — the HarfBuzz cluster-level-0 rule A4's caret
    /// mapping expects.
    /// </summary>
    internal void Ligate(ReadOnlySpan<int> componentIndices, uint ligatureGlyph)
    {
        var first = componentIndices[0];
        var last = componentIndices[^1];

        // Merge clusters to the minimum over the whole covered span (components + intervening marks).
        var minCluster = _clusters[first];
        for (var k = first + 1; k <= last; k++)
            if (_clusters[k] < minCluster) minCluster = _clusters[k];

        _glyphs[first] = ligatureGlyph;
        _classes[first] = (byte)GlyphClass.Ligature;
        for (var k = first; k <= last; k++) _clusters[k] = minCluster;

        // Compact out the non-first component slots, keeping everything else (marks).
        // componentIndices is ascending; walk source positions, dropping components 1..n-1.
        var write = first + 1;
        var ci = 1; // next component to drop
        for (var read = first + 1; read < _length; read++)
        {
            if (ci < componentIndices.Length && read == componentIndices[ci])
            {
                ci++; // drop this component slot
                continue;
            }
            if (write != read) CopySlot(read, write);
            write++;
        }
        _length = write;
    }

    private void CopySlot(int from, int to)
    {
        _glyphs[to] = _glyphs[from];
        _clusters[to] = _clusters[from];
        _masks[to] = _masks[from];
        _classes[to] = _classes[from];
        _advDeltas[to] = _advDeltas[from];
        _xOffsets[to] = _xOffsets[from];
        _yOffsets[to] = _yOffsets[from];
    }

    /// <summary>Reverse the run in place (logical → visual order for RTL). All parallel arrays reverse together.</summary>
    internal void Reverse()
    {
        Array.Reverse(_glyphs, 0, _length);
        Array.Reverse(_clusters, 0, _length);
        Array.Reverse(_masks, 0, _length);
        Array.Reverse(_classes, 0, _length);
        Array.Reverse(_advDeltas, 0, _length);
        Array.Reverse(_xOffsets, 0, _length);
        Array.Reverse(_yOffsets, 0, _length);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _glyphs.Length) return;
        var newSize = Math.Max(needed, _glyphs.Length * 2);
        Array.Resize(ref _glyphs, newSize);
        Array.Resize(ref _clusters, newSize);
        Array.Resize(ref _masks, newSize);
        Array.Resize(ref _classes, newSize);
        Array.Resize(ref _advDeltas, newSize);
        Array.Resize(ref _xOffsets, newSize);
        Array.Resize(ref _yOffsets, newSize);
    }
}
