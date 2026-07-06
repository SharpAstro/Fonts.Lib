namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// Drives lookup application for one layout table (GSUB or GPOS): the spec's outer walk
/// over the run in lookup-index order, plus the nested-lookup recursion that contextual
/// lookups (GSUB 5/6, GPOS 7/8) use to invoke other lookups at matched positions. One
/// runner per table per shaping pass; it holds no per-run mutable state, so it's cheap to
/// construct.
/// </summary>
internal sealed class LookupRunner
{
    // HarfBuzz caps nested lookup recursion at 6; deeper is treated as a no-op (a malformed
    // or pathological font) rather than risking unbounded recursion.
    private const int MaxNestingDepth = 6;

    // GSUB type 8 is Reverse Chaining Contextual Single Substitution — applied back-to-front.
    // (GPOS type 8 is chained context, applied forward like the others.)
    private const ushort GsubReverseChainType = 8;

    private readonly ShapingFont _font;
    private readonly LayoutTable _table;
    private readonly bool _isSubstitution;

    public LookupRunner(ShapingFont font, LayoutTable table, bool isSubstitution)
    {
        _font = font;
        _table = table;
        _isSubstitution = isSubstitution;
    }

    /// <summary>The font whose GDEF/metrics the appliers read.</summary>
    public ShapingFont Font => _font;

    /// <summary>Run the planned lookups over the whole buffer, in lookup-index order.</summary>
    public void Run(ShapePlan.PlannedLookup[] planned, ShapeBuffer buffer)
    {
        foreach (var p in planned)
        {
            if (p.LookupIndex >= _table.Lookups.Length) continue;
            var lookup = _table.Lookups[p.LookupIndex];
            if (lookup.Subtables.Length == 0) continue;

            if (_isSubstitution && lookup.Type == GsubReverseChainType)
            {
                RunReverse(lookup, p.Mask, buffer);
                continue;
            }

            for (var i = 0; i < buffer.Length;)
            {
                if ((buffer.MasksMutable[i] & p.Mask) == 0 || Skips(lookup, buffer, i))
                {
                    i++;
                    continue;
                }
                if (!ApplyLookup(lookup, buffer, ref i, depth: 0)) i++;
            }
        }
    }

    // Reverse chaining single subst walks end→start; each match substitutes in place
    // (1→1, no length change), so the index steps down whether or not a match occurred.
    private void RunReverse(Lookup lookup, ushort mask, ShapeBuffer buffer)
    {
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            if ((buffer.MasksMutable[i] & mask) == 0 || Skips(lookup, buffer, i)) continue;
            foreach (var subtable in lookup.Subtables)
                if (GsubApplier.ApplyReverseChain(this, lookup, subtable.Span, buffer, i)) break;
        }
    }

    /// <summary>Apply a lookup's subtables at position <paramref name="i"/> (first match wins),
    /// advancing <paramref name="i"/> past the output on success.</summary>
    public bool ApplyLookup(Lookup lookup, ShapeBuffer buffer, ref int i, int depth)
    {
        foreach (var subtable in lookup.Subtables)
        {
            if (_isSubstitution
                ? GsubApplier.Apply(this, lookup, subtable.Span, buffer, ref i, depth)
                : GposApplier.Apply(this, lookup, subtable.Span, buffer, ref i, depth))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Apply the lookup at <paramref name="lookupIndex"/> at a fixed <paramref name="position"/>
    /// — a contextual lookup's nested action. Returns whether it applied; the buffer may grow
    /// or shrink. The feature mask isn't re-checked (the enclosing context already decided to
    /// run), but lookupFlag skipping inside the nested lookup still applies.
    /// </summary>
    public bool ApplyNested(int lookupIndex, ShapeBuffer buffer, int position, int depth)
    {
        if (depth >= MaxNestingDepth || (uint)lookupIndex >= (uint)_table.Lookups.Length) return false;
        var lookup = _table.Lookups[lookupIndex];
        if (lookup.Subtables.Length == 0 || (uint)position >= (uint)buffer.Length) return false;
        var i = position;
        return ApplyLookup(lookup, buffer, ref i, depth + 1);
    }

    private bool Skips(Lookup lookup, ShapeBuffer buffer, int i)
        => lookup.Flags.SkipsGlyph(_font.Gdef, buffer.GlyphsMutable[i],
            (GlyphClass)buffer.ClassesMutable[i], lookup.MarkFilteringSet);
}
