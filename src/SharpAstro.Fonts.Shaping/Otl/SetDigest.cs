namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// A HarfBuzz-style set digest: a small, probabilistic (Bloom-filter-like) summary of a set of
/// glyph ids. Its job is to cheaply reject a lookup at a glyph before paying for that lookup's
/// coverage binary search and GDEF class lookup — the per-(glyph, lookup) cost that dominates
/// shaping a run (every enabled GSUB/GPOS lookup otherwise probes its coverage at every position).
///
/// <para>The digest is the union of three independent 64-bit masks, each keyed on a different
/// 6-bit window of the glyph id (shifts {4, 0, 9}). A glyph is <em>added</em> by setting one bit
/// in each mask; a glyph <em>may be present</em> only when all three masks have its bit set.
/// The test is one-sided — <see cref="MayContain"/> never returns false for a glyph that was
/// actually added (no false negatives) — so skipping a lookup when <see cref="MayContain"/> is
/// false is always correct. It only ever yields false positives, which fall through to the real
/// coverage probe exactly as before, so behavior is preserved and only wasted work is removed.</para>
///
/// <para>Ported from HarfBuzz's <c>hb_set_digest_t</c> (hb-set-digest.hh); 24 bytes, no heap.</para>
/// </summary>
internal struct SetDigest
{
    // The three windows of the glyph id HarfBuzz's default digest samples. Distinct shifts make a
    // false positive in one mask unlikely to coincide with false positives in the others.
    private const int Shift0 = 4;
    private const int Shift1 = 0;
    private const int Shift2 = 9;

    private ulong _mask0;
    private ulong _mask1;
    private ulong _mask2;

    // The single-bit mask a glyph maps to within one window (its bucket among the 64).
    private static ulong BitFor(uint glyph, int shift) => 1UL << (int)((glyph >> shift) & 63);

    /// <summary>Add a single glyph id to the digest.</summary>
    public void Add(uint glyph)
    {
        _mask0 |= BitFor(glyph, Shift0);
        _mask1 |= BitFor(glyph, Shift1);
        _mask2 |= BitFor(glyph, Shift2);
    }

    /// <summary>Add an inclusive glyph-id range [<paramref name="lo"/>, <paramref name="hi"/>]
    /// (<paramref name="lo"/> ≤ <paramref name="hi"/>) — coverage format 2 stores ranges, so this
    /// avoids expanding them to individual glyphs.</summary>
    public void AddRange(uint lo, uint hi)
    {
        AddRange(ref _mask0, lo, hi, Shift0);
        AddRange(ref _mask1, lo, hi, Shift1);
        AddRange(ref _mask2, lo, hi, Shift2);
    }

    // Per-window range fill (HarfBuzz's trick): if the range spans every bucket, saturate the
    // window; otherwise set every bit from bucket(lo)..bucket(hi) inclusive. The expression
    // mb + (mb - ma) - (mb < ma) equals 2^(hiBucket+1) - 2^(loBucket), which is all bits in
    // [loBucket, hiBucket] — and stays correct when the low 6 bits wrap (lo and hi straddle a
    // 64-bucket boundary), where mb < ma and the extra -1 rotates the run across the wrap.
    private static void AddRange(ref ulong mask, uint lo, uint hi, int shift)
    {
        if ((hi >> shift) - (lo >> shift) >= 63)
        {
            mask = ulong.MaxValue;
            return;
        }
        var ma = BitFor(lo, shift);
        var mb = BitFor(hi, shift);
        mask |= mb + (mb - ma) - (mb < ma ? 1UL : 0UL);
    }

    /// <summary>Force the digest to match every glyph. The safe fallback when a lookup's entry
    /// coverage can't be enumerated — a match-all digest is never wrongly skipped (it only forgoes
    /// the optimization for that lookup), whereas an under-filled digest would drop real matches.</summary>
    public void SaturateAll()
    {
        _mask0 = ulong.MaxValue;
        _mask1 = ulong.MaxValue;
        _mask2 = ulong.MaxValue;
    }

    /// <summary>Whether <paramref name="glyph"/> might be in the set. A false result means the glyph
    /// is definitely absent (safe to skip); a true result means "probe to be sure".</summary>
    public readonly bool MayContain(uint glyph)
        => (_mask0 & BitFor(glyph, Shift0)) != 0
        && (_mask1 & BitFor(glyph, Shift1)) != 0
        && (_mask2 & BitFor(glyph, Shift2)) != 0;
}
