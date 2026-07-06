using System.Collections.Concurrent;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// A font prepared for shaping: wraps an <see cref="OpenTypeFont"/> with its parsed
/// GSUB/GPOS/GDEF tables (read via <see cref="OpenTypeFont.TryGetTable"/>) and a
/// cache of resolved <see cref="ShapePlan"/>s. Create once per face and reuse —
/// construction parses the layout tables; shaping itself allocates nothing.
///
/// <para>Thread-safe after construction (immutable tables + concurrent plan cache),
/// matching <see cref="OpenTypeFont"/>'s own guarantees.</para>
/// </summary>
public sealed class ShapingFont
{
    /// <summary>The underlying face (cmap, metrics, outlines).</summary>
    public OpenTypeFont Font { get; }

    internal LayoutTable? Gsub { get; }
    internal LayoutTable? Gpos { get; }
    internal GdefTable Gdef { get; }

    // Lookup runners are immutable per font (font + table + substitution flag), so build them once
    // here instead of allocating a pair on every shape call. Null when the table is absent.
    internal LookupRunner? GsubRunner { get; }
    internal LookupRunner? GposRunner { get; }

    private readonly ConcurrentDictionary<(Tag Script, ShapeDirection Direction), ShapePlan> _plans = new();

    private ShapingFont(OpenTypeFont font, LayoutTable? gsub, LayoutTable? gpos, GdefTable gdef)
    {
        Font = font;
        Gsub = gsub;
        Gpos = gpos;
        Gdef = gdef;
        GsubRunner = gsub is not null ? new LookupRunner(this, gsub, isSubstitution: true) : null;
        GposRunner = gpos is not null ? new LookupRunner(this, gpos, isSubstitution: false) : null;
    }

    /// <summary>True when the font has a usable GSUB (ligatures/alternates possible).</summary>
    public bool HasSubstitution => Gsub is not null;

    /// <summary>True when the font has a usable GPOS (kerning/mark positioning possible).</summary>
    public bool HasPositioning => Gpos is not null;

    // GSUB Extension is lookup type 7 of max 8; GPOS Extension is type 9 of max 9.
    private static readonly Tag GsubTag = new("GSUB");
    private static readonly Tag GposTag = new("GPOS");
    private static readonly Tag GdefTag = new("GDEF");

    /// <summary>
    /// Prepare <paramref name="font"/> for shaping. Never throws on layout-table
    /// problems: a malformed GSUB/GPOS/GDEF parses to null/empty and shaping
    /// degrades to plain cmap mapping — a broken font is never worse than unshaped.
    /// </summary>
    public static ShapingFont Create(OpenTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);

        LayoutTable? gsub = null;
        if (font.TryGetTable(GsubTag, out var gsubData))
            gsub = LayoutTable.Parse(gsubData, extensionLookupType: 7, maxLookupType: 8);

        LayoutTable? gpos = null;
        if (font.TryGetTable(GposTag, out var gposData))
            gpos = LayoutTable.Parse(gposData, extensionLookupType: 9, maxLookupType: 9);

        var gdef = GdefTable.Empty;
        if (font.TryGetTable(GdefTag, out var gdefData))
            gdef = GdefTable.Parse(gdefData.Span);

        return new ShapingFont(font, gsub, gpos, gdef);
    }

    /// <summary>Resolved lookup plan for (<paramref name="script"/>, <paramref name="direction"/>),
    /// cached. The feature set comes from the shaper <see cref="Shaper.SelectShaper"/> picks for
    /// the script, so the plan always matches the shaper that will run it.</summary>
    public ShapePlan GetPlan(Tag script, ShapeDirection direction)
    {
        var key = (script, direction);
        // Cache-hit fast path: DrawText resolves a plan per run per frame, so skip the GetOrAdd
        // machinery (and any delegate allocation) on the overwhelmingly common hit.
        if (_plans.TryGetValue(key, out var plan)) return plan;
        // Miss: build via a STATIC factory + state arg so no `this`-capturing delegate is allocated.
        return _plans.GetOrAdd(key,
            static (k, self) => ShapePlan.Build(self.Gsub, self.Gpos, k.Script, k.Direction, Shaper.SelectShaper(k.Script)),
            this);
    }
}
