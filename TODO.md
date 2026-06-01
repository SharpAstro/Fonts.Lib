# TODO

Living document of deliberately-deferred work and known limitations.

## Known limitations

Listed by table / area.

### COLR v1
- **`PaintRotate` / `PaintScale` axis-around-center scaling** is
  approximate when `gradXform` includes non-uniform scale (the
  `rScale` heuristic in `ColrSvgWriter.AsRadialGradient` linearizes a
  matrix that may shear).
- **HSL non-separable composite modes** (`Hue`, `Saturation`, `Color`,
  `Luminosity`) fall back to `SrcOver`.

### COLR-as-SVG
- **Sweep gradients** fall back to a solid color from the middle stop
  (SVG has no native conic gradient).

### CBDT
- **Image format 19** (`PngOnly`) only works when the parent CBLC index
  subtable is format 2 (which provides shared `bigMetrics`).
- **CBLC index formats 4 and 5** (sparse glyph layouts) parse but
  return empty subtables. Not seen in real-world emoji fonts so far.
- **sbix table** (Apple per-glyph PNG strikes) not implemented — no
  Apple emoji fixture in the corpus to validate against.

### Variable fonts
- **CFF2** (`blend` operator, uint32 INDEX counts) not implemented.
  All variable fonts in our corpus are TrueType-based; no CFF2 fixture.

### CFF / Type 2
- **Flex operators** (`hflex`, `flex`, `hflex1`, `flex1`) silently
  consume args but emit no curves. Rare; affects extremely-detailed
  outlines on a few high-end OTFs.
- **Math / conditional ops** (`and`, `or`, `ifelse`, `abs`, `add`,
  `sub`, `div`, `neg`, `eq`, `drop`, `put`, `get`, `mul`, `sqrt`,
  `dup`, `exch`, `index`, `roll`, `random`) silently consume args.
  Vanishingly rare in real glyph charstrings.

### Hinting (TrueType bytecode)
The interpreter implements essentially the full instruction set (push /
arith / logic / round, MDRP/MIRP, IP/SHP/SHC/SHZ/SHPIX, MSIRP/ALIGNRP,
IUP, MIAP/MDAP, DELTAP/DELTAC, SROUND/S45ROUND, CALL/LOOPCALL/FDEF/IDEF,
GETINFO/INSTCTRL/SCANCTRL). It is interpreted in v40 / grayscale mode.
Known approximations vs FreeType:
- **Engine compensation** (the per-render-mode distance bias added before
  rounding MDRP/MIRP) is not applied — ~0 in grayscale/v40, so immaterial
  here; matters only for B&W conformance (`RoundMode.cs`).
- **Phantom-point touched flags**: SHZ shifts phantom points via the same
  `MovePoint` that sets touched flags, where the spec shifts without
  touching. Cumulative effect acceptable in practice (`Interpreter.cs`).
- **No FreeType conformance oracle.** `HintingFoundationTests` only checks
  hinted width is within ~1 px; there is no per-point comparison against
  FreeType, so hinting accuracy is unverified against ground truth. A
  FreeType-reference harness is the prerequisite for any serious
  hinting-fidelity work.
- **Hinting does not reach the SDF render path.** `RenderSdf` builds the
  distance field from the UNHINTED outline (`DrawGlyph`), and consumers
  that render text as SDF (e.g. the pdf-viewer, `SdfTextThreshold = 0`)
  therefore get no grid-fitting. Only the bitmap path (`RenderGlyphHinted`)
  is hinted. Feeding hinted outlines into `RenderSdf` is an open experiment
  (uncertain payoff — SDF undersamples tiny stems regardless).

## Out of scope

These are NOT planned for SharpAstro.Fonts and would warrant a
separate library if needed.

- **HarfBuzz-equivalent shaping** — GSUB lookup-types beyond
  pair-kerning, complex script shaping (Arabic, Indic, Thai, Hebrew),
  bidi reordering, line breaking, layout. SharpAstro.Fonts matches
  FreeType's scope; HarfBuzz is a separate concern.
- **Auto-hinter** — only if the v40 interpreter proves insufficient.
- **BDF, PCF, PFR, Windows FNT/FON** — legacy formats with no use case.
- **gxvalid / otvalid** validators — tools, not runtime.

## Performance backlog

Benchmark suite in `benchmarks/SharpAstro.Fonts.Benchmarks/`.

### Done
- ~~`ColrRenderer.RenderPaintGlyph` O(N²)~~ — now O(M²) via bbox mask.
- ~~SDF rasterizer brute-force~~ — SIMD-vectorized inner loop (3× faster).
- ~~`OutlineVariation.Apply` scratch alloc~~ — ArrayPool + ImmutableArray sharing.
- ~~Hinting CALL/LOOPCALL body copy~~ — zero-alloc via Execute offset/length.

### Remaining
- **`GvarTable.LoadGlyphTuples`** allocates `short[]` delta arrays per
  tuple per call. Could pool via ArrayPool or change TupleVariation to
  rent/return.
- **`Type2CharstringInterpreter`** stack is `double[513]` per call.
  Pool it.
- **`SmoothRasterizer.EdgeCollector`** allocates 4 `float[]` per glyph;
  could pool.
- **CFF `DrawGlyph` sink allocation** — 4,216 B per call (internal
  `List<>`). Pool or pre-size based on charstring hints.
- **COLR v1 `RenderColor` at large sizes** — 1.35 ms / 1.68 MB at
  128px. Per-layer rasterization allocates a full bitmap per paint
  layer. Consider compositing-in-place.
- **PNG decode** in CBDT — `StbImageSharp.ImageResult.FromMemory`
  takes `byte[]`; we `.ToArray()` the slice. Check for a span overload.
- **WOFF1 zlib decompression** — currently uses `MemoryStream` +
  `ZLibStream.Read(Span<byte>)`, which requires one `.ToArray()` of
  the compressed span to satisfy the `Stream` input. No span-based
  zlib API in the .NET 10 BCL. Three options; see "Deferred decisions"
  below. Low-priority — WOFF1 load is a cold path.

None of these are blocking — the immutable / lock-free design is the
non-negotiable invariant; performance is a knob to turn after.

## Deferred decisions

### WOFF1 zlib: wait for .NET 11 span API

**Decision:** wait. Revisit when `net11.0` ships.

`BrotliDecoder.TryDecompress(ReadOnlySpan<byte>, Span<byte>, out int)` gave
us a zero-copy pure-span Brotli path for WOFF2 (already applied in
`Woff2Reader.cs`). The matching zlib API is approved but not yet
shipped:

- [dotnet/runtime#62113](https://github.com/dotnet/runtime/issues/62113) —
  API proposal for `ZLibDecoder` / `DeflateDecoder` / `GZipDecoder`.
  Status: `api-approved`, milestone 11.0.0, `in-pr`.
- [dotnet/runtime#123145](https://github.com/dotnet/runtime/pull/123145) —
  Implementation PR. Status: `CHANGES_REQUESTED`. Functionally complete,
  backed by the same zlib-ng native the current `ZLibStream` uses.

When it ships, the swap in `WoffReader.DecompressZlib` is a one-liner,
symmetric to the Brotli fix — no `MemoryStream`, no `.ToArray()`.

### Alternatives considered (and why not now)

1. **Copy from dotnet/runtime** — rejected. The managed C# files are
   MIT but they P/Invoke into the native `CompressionNative` /
   zlib-ng shared library. Not pure managed; can't just vendor the C#.

2. **Vendor StbImageSharp's inflate** (`StbImage.Generated.Zlib.cs`,
   ~550 lines, public domain, AOT-safe). Technically viable — we
   already transitively depend on StbImageSharp for CBDT PNG decode.
   Rejected for now because:
   - Uses `unsafe` pointer arithmetic (style convention: no unsafe in
     our own code; `AllowUnsafeBlocks` not set on `SharpAstro.Fonts.csproj`)
   - WOFF1 load is not a hot path in benchmarks
   - .NET 11 will ship the proper API soon

3. **SharpZipLib's managed inflate** — MIT, pure managed, no unsafe,
   ~1500-3000 lines across 6-8 files. Larger footprint than StbSharp
   and needs a span-adapter shim. Overkill for a cold-path fix.

## SDF quality backlog

Items derived from the Acko ESDT article
(<https://acko.net/blog/subpixel-distance-transform>). Most of the
article does **not** apply: ESDT exists to patch a binary-bitmap → EDT
pipeline, and `SdfRasterizer.Rasterize` already computes analytic
distance from each pixel center to the flattened path edges, so the
gray-pixel / subpixel-offset / commutativity errors Acko corrects do
not arise. The list below is the subset that *would* still be a real
improvement.

### Asymmetric zero level

`SdfRasterizer.cs:104-109` (and the byte path at `:172-176`) maps
`[-spread, +spread]` symmetrically to `[0, 1]` with edge at 0.5.
Acko argues for placing zero at ~75% gray since dilation/outline is
more common than contraction. With `spread=4` and 8-bit storage, that
trades inside-precision for ~2× outline range. Worth doing only once
we actually outline/glow text (currently we don't). Cross-cuts the
shader threshold in `SdlVulkan.Renderer/VkPipelineSet.cs:160`.

### Adaptive raster size

`SdlVulkan.Renderer/VkSdfFontAtlas.cs:37` hardcodes
`SdfRasterSize = 128f` for every glyph. Acko's heuristic is
`1.5 × display size, rounded up to next power of two`. Fixed-size is
simpler and atlas-friendly; the cost is curve facets becoming visible
at display sizes ≫ 128 px, since `EdgeCollector`'s flatness tolerance
is set in raster-pixel units. Lives entirely in
`SdlVulkan.Renderer`, not Fonts.Lib — listed here for visibility.

### Ink-bleed simulation at small sizes

Optional aesthetic: 0.25 px outward bleed at ≥32 px display size,
tapered to 0 below. Implementable as a bias on the smoothstep
threshold in the SDF fragment shader
(`SdlVulkan.Renderer/VkPipelineSet.cs:149-162`) keyed on quad scale.
Not the same thing as the recent `ink-left baseline` work, which is
positional. Purely visual; defer until someone complains about
small-text crispness.

### Already correct

- **`fwidth`-based AA band** — `VkPipelineSet.cs:159` uses
  `fwidth(dist)`, which absorbs both texel size and on-screen quad
  scale automatically. Matches Acko's "GPU coordinates of SDF texture
  pixels" recommendation.
- **Bilinear sampler + `R8_Unorm`** — `VkSdfFontAtlas.cs:374-389`,
  canonical SDF sample-side setup.
- **Curve handling** — analytic distance to flattened segments, not
  to a binary mask. The whole article's premise (recovering precision
  lost in binarization) does not apply.

## Test coverage backlog

- **Composite glyphs in variable fonts** (e.g. `é` in Roboto Flex) —
  visual regression baseline for diacritic positioning at non-default axes.
- **Bidirectional / RTL text fixtures** — Arabic / Hebrew cmap coverage.
