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
  hinting-fidelity work. (`FreeTypeBindings` is a sibling repo in the org root,
  so the binding layer already exists — this is a harness, not a port.)
- **Three compounding interpreter defects — FIXED.** The `g`/`x` hang, the `t`
  1-pixel collapse and the deformed Arabic letterforms were all one story, and
  each defect hid the next. Kept because the *reason* nothing caught them is the
  reusable lesson:
  - `Zone`'s constructor never assigned `PointCount`. The glyph zone gets its
    count set by `HintingPipeline`, but the twilight zone is only ever
    constructed — so zone 0 reported 0 points, every twilight bounds-check
    failed, and the whole zone silently became a no-op. Fonts stage reference
    positions there, so a broad class of hinting simply did nothing.
  - `ALIGNRP` returned on that failed bounds-check **without popping its
    operands**. Fonts drive ALIGNRP from a `LOOPCALL`'d helper, so the surplus
    accumulated one entry per call until the enclosing "call until the stack
    drains" loop (`DEPTH … LT … JROT`) could never reach its exit depth. That
    was the hang. `ExecIp` / `ExecShp` already drained correctly on the same
    kind of guard; ALIGNRP was the lone inconsistency. The rule the code now
    states explicitly: **a guard may skip an instruction's effect, never its
    stack effect.**
  - The `cvt ` table was read as `ushort`, but it is an array of FWORD —
    *signed* int16. 26 of NotoSans-Regular's 150 control values are negative and
    each became a huge positive; NotoSansArabic-Regular has 4 of 20. Defect 1
    masked this completely, because with twilight dead the corrupt values never
    reached a point position. DejaVuSans has no negative entries at all, which
    is exactly why it looked clean throughout and NotoSans did not.
  - **Why the suite missed it:** every test compared hinted output against
    *itself* or against a stored baseline. Nothing asserted the one invariant
    that makes hinting hinting — that grid-fitting nudges an outline onto the
    pixel grid rather than resizing it. `HintingCorrectnessTests.HintedGlyphs_-
    StayInProportionToUnhinted` is that assertion; it found 139 blown-up
    (glyph, ppem) pairs in NotoSans-Regular, the worst 80x taller than unhinted.
    Prefer invariants over baselines for this kind of defect: a baseline happily
    records corruption as the expected answer.
  - Still worth doing, and now unblocked rather than urgent: the FreeType
    conformance oracle above. These three were found by reading a trace, which
    works for catastrophic breakage and not for subtle mis-hinting.
- **Arabic letterform deformation — fixed in principle, unconfirmed by eye.**
  NotoSansArabic-Regular carried 4 corrupted control values, so the CVT defect
  demonstrably applied to it; it no longer trips any proportion or budget check.
  But the original report was visual (interior strokes displaced while bounding
  boxes stayed byte-identical), and no self-comparison metric can settle it —
  hinted-vs-unhinted pixel divergence at 24ppem is indistinguishable from
  legitimate grid-fitting. Confirming it means putting it beside the browser
  again in the web demo. Until someone does, treat this one as untested.
- **Hinting does not reach the SDF render path.** `RenderSdf` builds the
  distance field from the UNHINTED outline (`DrawGlyph`), and consumers
  that render text as SDF (e.g. the pdf-viewer, `SdfTextThreshold = 0`)
  therefore get no grid-fitting. Only the bitmap path (`RenderGlyphHinted`)
  is hinted. Feeding hinted outlines into `RenderSdf` is an open experiment
  (uncertain payoff — SDF undersamples tiny stems regardless).

### AutoCAD SHX
Both text layouts (`unifont`, `bigfont`) and the whole opcode set are
implemented and validated against 4,428 real faces. The gaps are all at the
edges, and two of them are inference rather than specification:

- **`shapes` symbol libraries are not readable.** Rejected by header with
  `NotSupportedException`. They are addressed by shape number from a DWG, so
  supporting them means a different lookup API (`TryGetShape(int number)`), not
  a different parser. Worth doing only if a caller needs symbol lookup — but
  note they are the *majority* of `.shx` files in the wild (3,669 of 4,428 in
  the survey), so the demand may well arrive.
- **bigfont composed-subshape placement is inferred, not specified.** The
  extended form (`0x07 0x00 hi lo base_x base_y width height`) is read as
  "offset by (base_x, base_y), scale into a width x height box against
  `above`", derived from corpus statistics — `height == above` dominates, with
  `width` varying, i.e. full-height radicals of differing widths. Exact for
  that dominant case; the non-square minority is plausible but unverified. The
  parent pen is restored afterwards, which matches `base_x`/`base_y` being 0
  in the plurality of cases but is likewise inferred.
- **Clockwise fractional arcs (`0x0B` with the sign bit) are unverified.** The
  offsets are taken as signed by the sweep direction, which is the reading that
  makes `0x0B` degenerate exactly to `0x0A` when both offsets are zero. That
  constraint pins the counterclockwise case down completely; nothing in the
  corpus isolates the clockwise one.
- **Codes are opaque for bigfont.** Lead-byte ranges identify the encoding
  *family* (`0x81-0x9F, 0xE0-0xEA, 0xFD-0xFE` is Shift-JIS shaped, `0x80-0xFF`
  is Big5/GBK shaped) but never the codepage, so mapping a bigfont code to
  Unicode is left to the caller. A bundled codepage-guessing heuristic would be
  a separate feature.
- **No `0x0E` vertical-form rendering path beyond the interpreter.**
  `ShxTextOrientation.Vertical` runs the right commands, but there is no
  vertical layout support — no equivalent of `vmtx`/`VORG` — because SHX states
  none.

## Shaping (`SharpAstro.Fonts.Shaping`)

The separate pure-managed shaping engine — a distinct NuGet package layered
over `SharpAstro.Fonts`. This is the "separate library" the HarfBuzz note
below anticipated; it now exists, so that note no longer means shaping is
absent. What the engine does, and where it deliberately stops:

**Implemented:** the OpenType layout core — GSUB types 1–8, GPOS types 1–9,
GDEF (glyph classes + mark-filtering sets), context / chained-context /
reverse-chaining / extension lookups; ligatures, kerning, and mark-to-base /
-ligature / -mark; canonical mark reordering by combining class; Arabic
joining (`init`/`medi`/`fina`/`isol`); and the full UAX #9 bidirectional
algorithm with bidi-aware script itemization. Scripts that need no glyph
reordering shape correctly today: Latin, Greek, Cyrillic, CJK, Arabic,
Hebrew.

The limitations below live here (rather than scattered across the `.csproj`
description and XML-doc comments where they used to) so "what's left" is
answerable from the repo.

### Complex-script shapers — out of scope
No Indic, USE (Universal Shaping Engine), Khmer, Myanmar, Tibetan, or
Thai/Lao shaper. These need syllable segmentation plus base / reph / matra
glyph **reordering**; without it they fall through to `DefaultShaper` (which
runs GSUB/GPOS but reorders nothing) and will misrender. Matches the
package's own scope line ("Indic/USE out of scope") in
`SharpAstro.Fonts.Shaping.csproj`.

### No Unicode normalizer
The engine assumes **NFC input**: it reorders combining marks by canonical
combining class but never composes or decomposes (`ShaperBase.cs`,
`Shaper.cs`). There is no cmap-driven composition fallback (the equivalent of
HarfBuzz's shape-normalize pass), so decomposed input or fonts that only
cover composed forms can miss glyphs.

### Canonical-combining-class table is partial
`CanonicalCombiningClass` is only the Latin/Greek block, hand-transcribed;
codepoints outside it are treated as starters (CCC 0) and never reorder.
Non-Latin mark stacks need the full generated UCD table.

### GPOS variation deltas deferred
`ValueRecord` does not apply Device / VariationIndex (Item Variation Store)
deltas, so GPOS positioning on a **variable font at a non-default axis
position** uses default-instance values (`ValueRecord.cs` — "deferred IVT
path (plan non-goal)").

### No line-break / width / vertical UCD data
UAX #14 (line breaking), UAX #11 (East-Asian width), and UAX #50 (vertical
orientation) tables are not generated. These are layout-adjacent — a
line-layout engine's concern rather than the shaper's — but they are the
prerequisite for CJK / Thai line layout and vertical text.

## Bundled font licensing

Audited by reading each face's own `name` IDs 13/14 with this library, rather than
by filename or recollection — `BundledFontLicenceTests` keeps it honest and fails on
any new font that is not accounted for.

**The web demo is clean.** All seven faces it serves state SIL OFL 1.1. That test has
no exceptions list, which is what keeps `Merida.ttf` out: its provenance was never
established and it states no licence, so it stays a fixture and off the site.

**Five fixtures state no licence at all** — `D011A_subset`, `ISOCPEUR_subset`,
`Merida`, `Tahoma_subset`, `XXTIIT_Arial_subset`. All are small glyph subsets
extracted from PDFs, present since the first commit, each reproducing a specific
parser defect that no freely-licensed font in the corpus reproduces. Subsetting
discards the `name` table's licensing records along with everything else the PDF did
not need, so silence is not evidence the original was unlicensed.

**Open question, for a human rather than a test:** two of those originals are
proprietary — `Tahoma_subset` (Microsoft) and `XXTIIT_Arial_subset` (Monotype),
42 KB and 58 KB. Whether subset outlines at that size belong in a public test corpus
is a licensing judgement. Options if it is decided they do not: regenerate the
fixtures from a metrically-compatible libre face (Liberation Sans for Arial,
DejaVu Sans for Tahoma) and re-record the baselines, or keep the PDFs and generate
the subsets at test time rather than committing them. Note that
`XXTIIT_Arial_subset` backs the `SmallSize_ArialSubset_*` hinted baselines, so
replacing it means regenerating those.

## Out of scope

These are NOT planned for SharpAstro.Fonts and would warrant a
separate library if needed.

- **Shaping inside `SharpAstro.Fonts` itself** — the base library stays
  FreeType-scope (glyph loading, rasterization, hinting) and does no text
  shaping. OpenType shaping lives in the separate `SharpAstro.Fonts.Shaping`
  package (see the Shaping section above, which now covers the OT-layout
  core, Arabic joining, and UAX #9 bidi). A full HarfBuzz-equivalent —
  complex-script (Indic/USE/Thai) shaping — remains out of scope even there.
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
