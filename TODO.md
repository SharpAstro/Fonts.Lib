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

## Test coverage backlog

- **Composite glyphs in variable fonts** (e.g. `é` in Roboto Flex) —
  visual regression baseline for diacritic positioning at non-default axes.
- **TTC (TrueType Collection)** loading — already supported but untested.
- **Bidirectional / RTL text fixtures** — Arabic / Hebrew cmap coverage.
