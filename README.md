# SharpAstro.Fonts

A pure-managed, MIT-licensed C# library for loading and rendering OpenType /
TrueType fonts. Designed to replace the native FreeType2 dependency for
[DIR.Lib](https://github.com/SharpAstro/DIR.Lib) and related projects.

## Status

**Pre-alpha.** Phase 1 only (SFNT directory + cmap + head/maxp). Not yet
suitable for any production use. See [ROADMAP.md](ROADMAP.md) for the full
phase plan.

Until parity is reached, DIR.Lib continues to use `SharpAstro.FreeTypeBindings`.

## Goals

- 100% managed C#, AOT-compatible, no native dependencies.
- Thread-safe by construction (immutable font records, per-call rasterizer
  state, no global mutable state).
- MIT-licensed end to end. No code is copied from FreeType, SixLabors.Fonts,
  HarfBuzz, or other non-MIT sources.
- Feature parity with the FreeType subset that DIR.Lib actually exercises:
  TrueType + CFF/CFF2 outlines, hinting, COLR v0/v1, CBDT/sbix, variable
  fonts, PostScript Type 1 / CID Type 0 (for embedded PDF subset fonts).

## License

MIT — see [LICENSE](LICENSE).

## Specifications used as reference

All public, freely-implementable specs:

- [OpenType](https://learn.microsoft.com/typography/opentype/spec/) (Microsoft)
- [CFF / Type 2 charstrings](https://adobe-type-tools.github.io/font-tech-notes/) (Adobe)
- [WOFF](https://www.w3.org/TR/WOFF/) / [WOFF2](https://www.w3.org/TR/WOFF2/) (W3C)
- [COLR](https://learn.microsoft.com/typography/opentype/spec/colr) (Microsoft / Google)
- [Unicode UAX](https://www.unicode.org/reports/) (Unicode Consortium)

## Layout

```
src/SharpAstro.Fonts/         pure library
tests/SharpAstro.Fonts.Tests/ xUnit v3 tests + font fixtures
```
