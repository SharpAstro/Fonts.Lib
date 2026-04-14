# TODO

Living document of deliberately-deferred work and known limitations.
ROADMAP.md is the forward plan; this file is "what we know about, but
chose not to do (yet) and why."

## Completed phases (previously deferred)

### Phase 8 — TrueType bytecode hinting · **DONE**

Full v40 grayscale interpreter including Phase 8.5 follow-ups:
DELTAP1/2/3, DELTAC1/2/3, ISECT, per-(face, ppem) `HintingSnapshot`
cache with thread-safe per-call clone, engine compensation, v40
X-direction skip. Bug fixes: IUP double-shift, ScaleFunits truncation,
CutIn precedence, SHP/SHC/SHZ inversion.

## Known limitations

These ship in Phase 5/6/7 but with caveats. Listed by table.

### COLR v1 (Phase 5)
- ~~**`PaintLinearGradient`** `p2` direction~~ — **DONE** (commit `890fc3c`).
- ~~**`PaintComposite`** modes~~ — **DONE**: full Porter-Duff + separable
  modes (commit `bbc205f`). HSL non-separable modes (`Hue`, `Saturation`,
  `Color`, `Luminosity`) still fall back to `SrcOver`.
- ~~**`Var*` paint formats**~~ — **DONE**: wired via Item Variation Store
  (commit `bbc205f`).
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
- ~~**Composite glyph variation**~~ — **DONE** (commit `bbc205f`).
- ~~**HVAR / VVAR**~~ — **DONE** (commits `f74e2ad` / `bbc205f`).
- ~~**MVAR**~~ — **DONE** (commit `bbc205f`).
- ~~**`cvar`**~~ — **DONE** (commit `bbc205f`).
- ~~**Item Variation Store**~~ — **DONE** (commit `f74e2ad`).
- **CFF2** (`blend` operator, uint32 INDEX counts) not implemented.
  All variable fonts in our corpus are TrueType-based; no CFF2 fixture.

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

- ~~**`ColrRenderer.RenderPaintGlyph`** O(N²) full-surface iteration~~ —
  **DONE**: `RenderOutlineMask` now returns a compact bbox-sized mask;
  both `RenderPaintGlyph` and `FillGlyphMask` iterate only the mask
  region — O(M²) where M = glyph bbox.
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
- **CFF `DrawGlyph` sink allocation** —
  `Type2CharstringInterpreter` emits into an `IGlyphSink` that
  allocates 4,216 B per call (internal `List<>`). Pool or pre-size the
  backing list based on charstring hints. (Benchmark: 335 ns + 4.2 KB
  per CFF glyph vs 457 ns + 456 B for TrueType.)
- **COLR v1 `RenderColor` at large sizes** — 1.35 ms / 1.68 MB at
  128px. Per-layer rasterization allocates a full bitmap per paint
  layer. Consider a shared canvas with compositing-in-place to reduce
  allocation and rasterization passes. (Benchmark: 5× slower and 10×
  heavier than CBDT PNG decode at the same size.)

None of these are blocking — the immutable / lock-free design is the
non-negotiable invariant; performance is a knob to turn after.

## Test coverage backlog

- **Composite glyphs in variable fonts** (e.g. `é` in Roboto Flex) —
  add a visual regression baseline now that composite glyph variation
  is implemented. Verifies diacritic positioning at non-default axis values.
- **TTC (TrueType Collection)** loading — `OpenTypeFont.Load(data,
  faceOffset)` already supports it but no test exercises it.
- **Bidirectional / RTL text fixtures** — none of our tests exercise
  Arabic / Hebrew at the cmap level.
- ~~**CJK / cmap format 14 (variation selectors)**~~ — **DONE**: 4 CJK
  fixtures (NotoSansJP/KR/SC/TC), cmap format 14 parser, IVS lookup API,
  11 CJK baseline images (base + IVS variant glyphs).
