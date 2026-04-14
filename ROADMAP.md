# Roadmap

Each phase is "done" when its parity tests are 100% green against
FreeType-generated golden output for the test corpus.

## Phase 1 — SFNT skeleton + cmap *(in progress)*

- Big-endian span reader
- SFNT directory + table records (including TTC support)
- Required tables: `head`, `maxp`, `hhea`, `hmtx`, `name`, `OS/2`, `post`
- `cmap` subtables 0, 4, 6, 12, 14 (then 13)
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

## Phase 8 — TrueType bytecode interpreter *(core done)*

**Status update:** the interpreter now runs glyph hint programs end-to-end
via `OpenTypeFont.LoadHintedOutline(gid, ppem)` and
`RenderGlyphHinted(gid, ppem)`. Implemented opcodes cover the v40 grayscale
common path: stack/arithmetic/logic/storage/CVT, all round modes
(RTG/RTHG/RTDG/RUTG/RDTG/ROFF/SROUND/S45ROUND), full graphics state setters,
function defs / control flow, projection / freedom / dual vector setters
(SVTCA/SPVTCA/SFVTCA/SPVFS/SFVFS/SFVTPV/SPVTL/SFVTL/SDPVTL), and the
movement/query verbs MDAP, MIAP, MDRP, MIRP, ALIGNRP, ALIGNPTS, IP, IUP[xy],
SHP, SHC, SHZ, SHPIX, MSIRP, UTP, GC, MD, SCFS, FLIPPT, FLIPRGON, FLIPRGOFF,
INSTCTRL. **Deferred to Phase 8.5:** DELTAP1/2/3, DELTAC1/2/3 (per-ppem
fine adjustments), ISECT (line intersection — rare), per-(face, ppem) cache
(currently re-runs fpgm + prep on every render call), and a per-call
interpreter snapshot for lock-free concurrent rendering.

**Reactivated** after Phase 12 swap revealed that DIR.Lib's baseline
images depended on FT's hinting. Without hinting, glyphs land at their
"natural" sub-pixel position which is correct but visually shifts ~1
px from FT-baseline expectations. Matching FT pixel-perfectly requires
implementing the hint program execution path.

Scope (FreeType v40 grayscale mode):
- Interpreter state: operand stack (~256 deep), twilight zone, point
  storage (current + original outlines), CVT, Storage Area, function
  table, graphics state (zp0-2, rp0-2, projection / freedom / dual
  projection vectors, round state, scan control, loop counter, etc.).
- 3 program contexts: `fpgm` (runs once at face load), `prep` (runs
  on each size change), per-glyph instructions (during `Glyf.LoadGlyph`).
- ~150 v40-compatible opcodes (FreeType's modern default drops some
  legacy / debug ones).
- Hooks into outline loading: `Glyf.LoadGlyph` becomes
  `Glyf.LoadGlyph(uint, hintInterpreter?, ppem)` — interpreter runs
  glyph instructions over the glyph's points before returning the
  outline. CVT scaled to ppem inside `prep`.

Reference: FreeType source (`src/truetype/ttinterp.c`) + Apple TrueType
spec + Microsoft OpenType spec §TT instruction set.

Realistic effort: 2-3k LOC over multiple sessions. v40 mode skips some
legacy opcodes (DELTAC*, complex scan-conversion control, S45ROUND
edge cases). Scope can shrink further if specific opcodes turn out to
be unused by the corpus.

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
