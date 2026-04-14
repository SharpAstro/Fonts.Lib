# SharpAstro.Fonts

A pure-managed, MIT-licensed C# library for loading and rendering OpenType /
TrueType fonts. Designed to replace the native FreeType2 dependency for
[DIR.Lib](https://github.com/SharpAstro/DIR.Lib) and related projects.

## Status

**All phases complete.** The library has full feature parity with the FreeType
subset used by DIR.Lib, including TrueType hinting, CFF outlines, COLR v0/v1
color glyphs, bitmap emoji, variable fonts, and PostScript Type 1.
155 tests passing across outline, rasterizer, color, hinting, variation, and
CJK baseline suites.

## Goals

- 100% managed C#, AOT-compatible, no native dependencies.
- Thread-safe by construction (immutable font records, per-call rasterizer
  state, lock-free hinting snapshot cache).
- MIT-licensed end to end. No code is copied from FreeType, SixLabors.Fonts,
  HarfBuzz, or other non-MIT sources.
- Feature parity with the FreeType subset that DIR.Lib actually exercises.

## Supported formats

### Container formats

| Format | Notes |
|--------|-------|
| OpenType / TrueType (`.otf`, `.ttf`) | SFNT versions `0x00010000`, `OTTO`, `true`, `typ1` |
| TrueType Collection (`.ttc`) | Per-face offset loading |
| WOFF1 | Zlib-compressed SFNT |
| WOFF2 | Brotli + glyf/loca transform reconstruction |
| PostScript Type 1 (`.pfb`) | PFB binary container, eexec decryption |

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
src/SharpAstro.Fonts/         pure library
tests/SharpAstro.Fonts.Tests/ xUnit v3 tests + font fixtures
```
