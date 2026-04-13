# Roadmap

Each phase is "done" when its parity tests are 100% green against
FreeType-generated golden output for the test corpus.

## Phase 1 — SFNT skeleton + cmap *(in progress)*

- Big-endian span reader
- SFNT directory + table records (including TTC support)
- Required tables: `head`, `maxp`, `hhea`, `hmtx`, `name`, `OS/2`, `post`
- `cmap` subtables 0, 4, 6, 12 (then 13, 14)
- Public `OpenTypeFont` entry point with codepoint → glyph-id lookup

## Phase 2 — TrueType outlines

- `loca`, `glyf` parsing, composite resolution with affine transforms
- Outline → polygon flattener (recursive Bézier subdivision)
- Parity: outline coordinates byte-identical to FreeType
  `FT_Load_Glyph(NO_HINTING | NO_SCALE)` for every glyph in the corpus

## Phase 3 — Smooth rasterizer

- Anti-aliased rasterizer (analytic coverage)
- Mono fallback
- Parity: per-pixel ΔE ≤ 1 for ≥ 99 % of pixels at unhinted sizes

## Phase 4 — CFF1 + CFF2

- INDEX / DICT / Top DICT / Private DICT parsing
- Type 2 charstring interpreter (~40 operators)
- Local + global subroutines (with bias)
- FDSelect for CID-keyed CFF
- Closes the largest "still need FreeType" gap for OTF fonts

## Phase 5 — COLR v0 + v1 + CPAL

- `CPAL` palette parsing + selection
- COLR v0 layered glyphs
- COLR v1 paint tree (Solid, Linear/Radial/Sweep gradients, Glyph,
  ColrGlyph, Transform/Translate/Scale/Rotate/Skew, Composite)
- Gradient evaluator — port the math from
  `DIR.Lib/ColrV1Renderer.cs`, but operating on managed paint records
  (no pointer arithmetic)

## Phase 6 — CBDT / sbix bitmap glyphs

- `CBLC`/`CBDT`, `EBLC`/`EBDT`, `sbix` parsing
- PNG decode via [**StbImageSharp**](https://www.nuget.org/packages/StbImageSharp/)
  (already pinned at 2.30.15 in DIR.Lib's `Directory.Packages.props`)
- For raw zlib / PNG-predictor needs we can reference local
  `drawboard/pdf-viewer/src/IO.Lib` instead of vendoring

## Phase 7 — Variable fonts

- `fvar`, `avar`, `gvar`, `cvar`, `STAT`, `MVAR`/`HVAR`/`VVAR`
- Item variation store, region/tuple math
- Static instancer

## Phase 8 — TrueType bytecode interpreter *(deferred indefinitely)*

See [TODO.md](TODO.md#phase-8--truetype-bytecode-hinting--deprioritized-indefinitely)
for the cost/benefit analysis. Short version: ~5–8k LOC of the gnarliest
code in the library, primarily benefits 8–12 px body text in well-hinted
system fonts — a niche DIR.Lib doesn't live in. Modern AA + supersampling
covers the realistic use cases. Revisit only if a concrete
small-text-quality complaint surfaces against a non-DPI-scaled display.

## Phase 9 — PostScript Type 1 / Type 42 / CID Type 0

- Type 1 charstring interpreter (different from Type 2: absolute coords,
  `seac` accented composite, `lsb` op, etc.)
- eexec + charstring decryption (XOR-stream cipher)
- Type 42 (TrueType outlines wrapped in PostScript dict)
- CID Type 0 (Type 1 charstrings indexed by CID via FDArray)
- Removes the last format the FreeType-backed `FreeTypeGlyphRasterizer`
  could handle that we don't.

**Status check before starting:** every fixture in our test corpus
(8 TTFs + Source Sans CFF + Roboto Flex VF) renders end-to-end through
SharpAstro.Fonts. The "still need FreeType" gap is hypothetical until
proven by a real production font. Consider going straight to Phase 12
(see below) and adding Phase 9 only when a real Type 1 PDF embedded
font surfaces.

## Phase 10 — Stroker, SDF, kern, vmtx, GPOS-kern

- Outline stroker
- SDF rasterizer (GPU text)
- Legacy `kern` table
- GPOS lookup-type-2 (pair adjustment) only — full GPOS = shaping, separate

## Phase 11 — WOFF / WOFF2

- WOFF: zlib-compressed SFNT (use `System.IO.Compression.ZLibStream`)
- WOFF2: Brotli + custom `glyf`/`loca` transform
  (`System.IO.Compression.BrotliStream` already in BCL)

## Phase 12 — DIR.Lib swap

- Implement `ManagedFontRasterizer` in DIR.Lib using SharpAstro.Fonts
- Drop `SharpAstro.FreeTypeBindings` package reference
- Delete `DIR.Lib/ColrV1Renderer.cs` (replaced by managed paint tree from Phase 5)
- Either delete `FreeTypeGlyphRasterizer` entirely or keep as legacy fallback

## Out of scope

- HarfBuzz-equivalent shaping (GSUB lookups, complex scripts, bidi)
- Auto-hinter (write only if v40 interpreter is insufficient)
- BDF, PCF, PFR, Windows FNT/FON
- gxvalid / otvalid validators
- FreeType cache subsystem (use BCL caches if needed)

## Test corpus seed

Copied from `DIR.Lib/src/DIR.Lib.Tests/Fonts/`:

- `XXTIIT_Arial_subset.ttf` — Symbol-encoded PDF subset (charCode → PUA → GID)
- `Tahoma_subset.ttf` — Mac Roman trap-test
- `ISOCPEUR_subset.ttf` — engineering font subset
- `Merida.ttf` — straightforward TTF
- `DejaVuSans.ttf` — full Unicode coverage
- `Noto-COLRv1.ttf` — COLR v1 reference
- `NotoColorEmoji.ttf` — CBDT bitmap emoji
- `BabelStoneXiangqiColour.ttf` — COLR + non-Latin
