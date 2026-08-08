# SharpAstro.Fonts

A pure-managed, MIT-licensed C# library for loading and rendering OpenType /
TrueType fonts.

**[Try it in your browser →](https://sharpastro.github.io/Fonts.Lib/)** — the same
font file, string and pixel size rendered by this library (compiled to WebAssembly)
and by your browser, one above the other. Source: `src/SharpAstro.Fonts.Web`.

## Status

**All phases complete.** TrueType hinting, CFF outlines, COLR v0/v1 color
glyphs, bitmap emoji, variable fonts, PostScript Type 1, WOFF/WOFF2, and
CJK variation selectors. 594 tests passing across outline, rasterizer, color,
hinting, variation, CJK baseline, and shaping-conformance suites.

## Goals

- 100% managed C#, AOT-compatible, no native dependencies.
- **Cross-platform** — targets `net10.0`, runs on x64 (Windows, Linux, macOS)
  and ARM64 (Windows on ARM, Apple Silicon, Linux ARM64). SIMD hot paths
  use `Vector<T>` to auto-scale across SSE2 / AVX2 / AVX-512 / AdvSIMD.
- Thread-safe by construction (immutable font records, per-call rasterizer
  state, lock-free hinting snapshot cache).
- MIT-licensed end to end. No code is copied from FreeType, SixLabors.Fonts,
  HarfBuzz, or other non-MIT sources.
- Feature parity with the FreeType subset needed for PDF rendering and
  UI text.

## Supported formats

### Container formats

| Format | Notes |
|--------|-------|
| OpenType / TrueType (`.otf`, `.ttf`) | SFNT versions `0x00010000`, `OTTO`, `true`, `typ1` |
| TrueType Collection (`.ttc`) | Per-face offset loading |
| WOFF1 | Zlib-compressed SFNT |
| WOFF2 | Brotli + glyf/loca transform reconstruction |
| PostScript Type 1 (`.pfb`) | PFB binary container, eexec decryption |
| AutoCAD SHX (`.shx`) | `unifont` (keyed by code point) and `bigfont` (double-byte CJK). See below |

### AutoCAD SHX

The format DWG text styles use, and the reason SHX text in a plotted PDF arrives
as bare path geometry with no font object and no `/ToUnicode` behind it — which
makes it invisible to every text extractor. `ShxFont` is deliberately separate
from `OpenTypeFont`: it shares no tables, no `cmap` and no SFNT structure.

The one thing that behaves unlike every other format here: **SHX is stroked, not
filled**. A glyph is a pen path whose width comes from the graphics state (the
`w` operator in PDF), never from the font, and there is no filled counter — the
bowl of an `O` is a stroked circle, not two contours. So `TryGetGlyph` emits an
**open** path and never calls `Close()`. Consumers wanting geometry (text
extraction, shape matching, hit-testing) take it directly; consumers wanting
something fillable go through `TryGetStrokedOutline` or `RenderGlyph` with a
width of their choosing.

Shape libraries (`AutoCAD-86 shapes` — `simplex.shx`, `ACAD.SHX`, P&ID and survey
symbol sets) are **rejected** with `NotSupportedException`. They are not text
fonts: their records are addressed by shape number from a DWG rather than by
character code, so there is no character mapping to read.

Validated against 4,428 stock and third-party `.shx` files: 470,156 glyphs decode
without an exception, 99.0% of them producing geometry. That corpus cannot be
bundled (Autodesk's faces are their IP; this repo is MIT end to end), so CI runs
against two fixtures authored from scratch by `tools/make_shx_fixtures.py`. Point
`SHX_TEST_FONT_DIR` at a local directory of `.shx` files to run the breadth suite
in `ShxRealFaceTests`.

### OpenType tables

#### Core

| Tag | Coverage |
|-----|----------|
| `head` | Full |
| `maxp` | v0.5 (CFF) and v1.0 (TrueType) |
| `hhea` / `hmtx` | Full horizontal metrics |
| `vhea` / `vmtx` | Full vertical metrics |

#### Character mapping (`cmap`)

| Format | Description |
|--------|-------------|
| 0 | Byte encoding (256-entry) |
| 4 | Segmented BMP (most common) |
| 6 | Trimmed table |
| 12 | Segmented full UCS-4 |
| 14 | Unicode Variation Sequences (IVS / emoji VS) |

Not implemented: formats 2 (high-byte CJK legacy), 8 (mixed 16/32), 13 (last-resort).

#### Outlines

| Tag | Coverage |
|-----|----------|
| `loca` | Short and long offset formats |
| `glyf` | Simple glyphs (full flag/coordinate decoding), composite glyphs (all transform variants, scaled/unscaled component offsets, composite instructions) |
| `CFF ` | CFF1: Type 2 charstring interpreter, CID-keyed fonts (FDSelect format 0 + 3), global + local subroutines |

Not implemented: CFF2 (`blend` operator, uint32 INDEX counts).

#### TrueType hinting

| Tag | Coverage |
|-----|----------|
| `cvt ` | Full CVT array, scaled per ppem |
| `fpgm` | Executed once at face load |
| `prep` | Executed per size change |

Full v40 grayscale interpreter: ~150 opcodes including DELTAP1/2/3, DELTAC1/2/3,
ISECT, engine compensation, IUP, SHP/SHC/SHZ, all round modes, function defs,
projection/freedom/dual vector setters. Per-(face, ppem) `HintingSnapshot` cache
with thread-safe per-call clone.

#### Color

| Tag | Coverage |
|-----|----------|
| `COLR` | **v0**: base glyph + layer records. **v1**: all 32 paint formats including Var\* variants, full Porter-Duff composite modes (HSL non-separable modes fall back to SrcOver), ColorLine with Pad/Repeat/Reflect extend, VarIndexMap + embedded ItemVariationStore |
| `CPAL` | v0 and v1 headers, multiple palettes, BGRA→RGBA conversion |
| `CBLC` | Index formats 1, 2, 3. Formats 4/5 (sparse) parse but return empty |
| `CBDT` | Image formats 17, 18 (metrics + PNG), 19 (PNG-only with shared metrics) |

Not implemented: `sbix` (Apple bitmap strikes), non-PNG CBDT image formats.

#### Variable fonts

| Tag | Coverage |
|-----|----------|
| `fvar` | Axis definitions, named instances, normalization |
| `avar` | v1 segment maps (piecewise-linear remap) |
| `gvar` | Full tuple variation: shared/private point numbers, packed deltas, composite component-offset deltas, phantom points |
| `HVAR` | Advance-width variation via ItemVariationStore + DeltaSetIndexMap |
| `VVAR` | Advance-height variation (mirrors HVAR) |
| `MVAR` | Font-wide metric variation (all tags) |
| `cvar` | CVT hint-program variation (packed tuples targeting CVT indices) |

Not implemented: avar v2 (non-linear ItemVariationStore remapping).

#### Kerning / positioning

| Tag | Coverage |
|-----|----------|
| `kern` | Microsoft v0, format 0 (ordered pairs with binary search) |
| `GPOS` | LookupType 2 PairAdjustment: format 1 (per-glyph pair sets) and format 2 (class-based pair matrix), Coverage format 1+2, ClassDef format 1+2 |

Not implemented: Apple kern v1, kern format 2, GPOS lookup types 1/3–9.

#### Shared variation infrastructure

| Component | Coverage |
|-----------|----------|
| ItemVariationStore | Format 1, LONG_WORDS flag, piecewise-linear region scalars. Shared by HVAR, VVAR, MVAR, COLR v1 |
| DeltaSetIndexMap | Format 0 (uint16) and format 1 (uint32), variable entry sizes |

### Rasterizer

| Feature | Notes |
|---------|-------|
| Anti-aliased (smooth) | Analytic coverage rasterizer |
| Mono fallback | Binary threshold |
| SDF (signed distance field) | For GPU text rendering |
| COLR v0/v1 renderer | Full paint-tree walker into RGBA surface |

### PostScript Type 1

| Feature | Notes |
|---------|-------|
| `.pfb` binary container | Segment markers, eexec decryption |
| Type 1 charstrings | Full interpreter including `seac` accented composites |
| Encoding array + FontMatrix | Standard encoding, custom encoding |

Not implemented: `.pfa` ASCII format, standalone CID Type 0 (CID-keyed CFF is
supported via the CFF1 parser).

## License

MIT — see [LICENSE](LICENSE).

## Specifications used as reference

All public, freely-implementable specs:

- [OpenType](https://learn.microsoft.com/typography/opentype/spec/) (Microsoft)
- [CFF / Type 2 charstrings](https://adobe-type-tools.github.io/font-tech-notes/) (Adobe)
- [TrueType instruction set](https://learn.microsoft.com/typography/opentype/spec/tt_instructions) (Microsoft / Apple)
- [WOFF](https://www.w3.org/TR/WOFF/) / [WOFF2](https://www.w3.org/TR/WOFF2/) (W3C)
- [COLR](https://learn.microsoft.com/typography/opentype/spec/colr) (Microsoft / Google)
- [Unicode UAX](https://www.unicode.org/reports/) (Unicode Consortium)

## Layout

```
src/SharpAstro.Fonts/                   pure library
tests/SharpAstro.Fonts.Tests/           xUnit v3 tests + font fixtures
benchmarks/SharpAstro.Fonts.Benchmarks/ BenchmarkDotNet perf suite
```

## Development history

Developed in 12 phases, each validated against golden output:

1. SFNT skeleton + cmap
2. TrueType outlines (glyf/loca, composite glyphs)
3. Smooth rasterizer (analytic coverage)
4. CFF1 (Type 2 charstring interpreter, CID-keyed fonts)
5. COLR v0/v1 + CPAL (paint tree, gradients, composite modes)
6. CBDT bitmap glyphs (PNG emoji)
7. Variable fonts (fvar/avar/gvar/cvar/HVAR/VVAR/MVAR)
8. TrueType bytecode hinting (v40 grayscale, ~150 opcodes)
9. PostScript Type 1 (.pfb)
10. SDF rasterizer, kern/GPOS kerning, stroker, vertical metrics
11. WOFF / WOFF2
12. DIR.Lib integration (replaced FreeType native bindings)
