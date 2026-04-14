# TODO

Living document of deliberately-deferred work and known limitations.
ROADMAP.md is the forward plan; this file is "what we know about, but
chose not to do (yet) and why."

## Deferred phases

### Phase 8 — TrueType bytecode hinting · **CORE LANDED**

The interpreter foundation + the common-path verbs ship; see ROADMAP.md
for the current opcode coverage. **Phase 8.5 follow-ups:** DELTAP1/2/3
+ DELTAC1/2/3, ISECT, per-(face, ppem) cache, lock-free per-call
interpreter snapshot. Validation against DIR.Lib's `RenderAcceptanceTests`
baselines (16 tests fail without hinting in DIR.Lib's full-FT-removal
state) is the next downstream consumer task.

(Original cost/benefit analysis kept below for reference.)

**What it is:** Full TrueType bytecode interpreter — stack VM, ~200
opcodes, graphics state machine, twilight zone, `fpgm` / `prep` / per-glyph
hinting programs. ~5-8k LOC of the gnarliest code in the library.

**What it would help:**
- 8-12 px body text in well-hinted system fonts (MS, Source Code Pro, …)
- Stem consistency at very small sizes

**What it doesn't help:**
- Color emoji (COLR / CBDT) — no hinting involved
- Variable fonts at non-default axes — hint programs assume default instance
- Sizes ≥ ~16 px — supersampling AA already produces clean output
- High-DPI rendering

**Why skip:** DIR.Lib renders PDF text, chess pieces, color emoji. None
fit the "small body text in a system font" niche. FreeType itself
defaults to "v40" mode which intentionally drops most hinting.
`ttf-parser` (popular Rust library) ships without it. The juice is
not worth the squeeze for our scope.

**Revisit trigger:** specific user complaint about small-text rendering
quality on a non-DPI-scaled display. Then we re-scope.

## Known limitations

These ship in Phase 5/6/7 but with caveats. Listed by table.

### COLR v1 (Phase 5)
- **`PaintLinearGradient`** ignores the `p2` direction parameter. Most
  fonts have `p2` collinear with `p0p1` so the simplified projection
  matches; off-axis gradients render slightly wrong.
- **`PaintComposite`** modes other than `SrcOver` render as `SrcOver`.
  CSS `mix-blend-mode` mapping exists for the SVG path but is not
  emitted; the rasterizer doesn't support non-`SrcOver` blending at all.
- **`Var*` paint formats** (3, 5, 7, 9, 13, 15, 17, …) render nothing.
  Wire up alongside the variable-font Item Variation Store backlog
  (below).
- **`PaintRotate` / `PaintScale` axis-around-center scaling** is
  approximate when `gradXform` includes non-uniform scale (the
  `rScale` heuristic in `ColrSvgWriter.AsRadialGradient` linearizes a
  matrix that may shear).

### COLR-as-SVG (Phase 5)
- **Sweep gradients** fall back to a solid color from the middle stop
  (SVG has no native conic gradient; CSS `conic-gradient` exists but
  only on HTML `<div>`, not SVG `<linearGradient>`/`<radialGradient>`).
- **Composite modes** ignored as above.

### CBDT (Phase 6)
- **Image format 19** (`PngOnly`) only works when the parent CBLC index
  subtable is format 2 (which provides shared `bigMetrics`). For
  format-19 images under non-format-2 indexes, metrics fall back to 0.
- **CBLC index formats 4 and 5** (sparse glyph layouts) parse but
  return empty subtables. Not seen in real-world emoji fonts so far.
- **sbix table** (Apple per-glyph PNG strikes) not implemented — no
  Apple emoji fixture in the corpus to validate against.

### Variable fonts (Phase 7)
- **Composite glyph variation** — `gvar` deltas for composites encode
  component-anchor offsets via phantom points. Currently not applied;
  each component glyph still gets its own `gvar` deltas individually,
  producing mostly-correct accented forms with potentially-misaligned
  diacritics at extreme axis values.
- **HVAR / VVAR** (advance-width / vertical-metrics variation) not
  implemented. `hmtx.GetAdvanceWidth(gid)` returns the default-instance
  advance regardless of active variation. Affects layout precision at
  non-default weights; doesn't affect glyph rendering.
- **MVAR** (font-wide metric variation) not implemented.
- **`cvar`** (CVT hint-program deltas) not implemented — would only
  matter if Phase 8 lands.
- **CFF2** (`blend` operator, uint32 INDEX counts) not implemented.
  All variable fonts in our corpus are TrueType-based; no CFF2 fixture.
- **Item Variation Store** (used by HVAR/MVAR/COLRv1 `Var*` paints)
  not implemented. Unblocks a chunk of items above when added.

### CFF / Type 2 (Phase 4)
- **Flex operators** (`hflex`, `flex`, `hflex1`, `flex1`) silently
  consume args but emit no curves. Rare; affects extremely-detailed
  outlines on a few high-end OTFs.
- **Math / conditional ops** (`and`, `or`, `ifelse`, `abs`, `add`,
  `sub`, `div`, `neg`, `eq`, `drop`, `put`, `get`, `mul`, `sqrt`,
  `dup`, `exch`, `index`, `roll`, `random`) silently consume args.
  Vanishingly rare in real glyph charstrings.

## Out of scope

These are NOT planned for SharpAstro.Fonts and would warrant a
separate library if needed.

- **HarfBuzz-equivalent shaping** — GSUB lookup-types beyond
  pair-kerning, complex script shaping (Arabic, Indic, Thai, Hebrew),
  bidi reordering, line breaking, layout. SharpAstro.Fonts matches
  FreeType's scope; HarfBuzz is a separate concern.
- **Auto-hinter** — write only if Phase 8 (which we're not doing) ever
  proves insufficient.
- **BDF, PCF, PFR, Windows FNT/FON, BZIP2/LZW-compressed PCF** —
  legacy formats with no use case in DIR.Lib.
- **gxvalid / otvalid** validators — tools, not runtime.
- **FreeType cache subsystem** — irrelevant in managed land; use BCL
  caches if needed.

## Performance backlog

After Phase 12 swap, profile DIR.Lib end-to-end and fix what's slow.
Likely candidates:

- **`ColrRenderer.RenderPaintGlyph`** iterates all surface pixels for
  each layer. Could iterate just the mask bbox — easy O(N²) → O(M²)
  win where M = mask size, N = surface size.
- **`OutlineVariation.Apply`** allocates two `float[pointCount]` +
  `bool[pointCount]` per glyph render. `ArrayPool<T>.Shared` would
  drop variable-font heavy-render allocation pressure.
- **`GvarTable.LoadGlyphTuples`** allocates per call. Could pool tuple
  scratch.
- **`Type2CharstringInterpreter`** stack is `double[513]` per call.
  Pool it.
- **`SmoothRasterizer.EdgeCollector`** allocates 4 `float[]` per glyph;
  could pool.
- **PNG decode** in CBDT — `StbImageSharp.ImageResult.FromMemory`
  takes `byte[]` (not `ReadOnlySpan<byte>`); we currently `.ToArray()`
  the slice. Investigate whether StbImageSharp has a span overload.

None of these are blocking — the immutable / lock-free design is the
non-negotiable invariant; performance is a knob to turn after.

## Test coverage backlog

- **Composite glyphs in variable fonts** (e.g. `é` in Roboto Flex) —
  add a visual regression baseline now that we know the
  diacritic-misalignment limitation exists.
- **TTC (TrueType Collection)** loading — `OpenTypeFont.Load(data,
  faceOffset)` already supports it but no test exercises it.
- **Bidirectional / RTL text fixtures** — none of our tests exercise
  Arabic / Hebrew at the cmap level.
