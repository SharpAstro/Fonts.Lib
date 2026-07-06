# Vendored Unicode Character Database (UCD)

These are **checked-in** UCD source files — no git submodule, no build-time download.
`tools/UcdGen` reads them offline and emits the RVA property tables under
`src/SharpAstro.Fonts.Shaping/Ucd/*.g.cs`. The generated files are committed and reviewed
like any code; the build never runs the generator.

## Pin

**UCD 17.0.0** (latest stable; final files dated 2025-08-15). Source:
<https://www.unicode.org/Public/17.0.0/ucd/>.

| File | Property extracted | Consumed by |
|---|---|---|
| `17.0.0/UnicodeData.txt` | Canonical_Combining_Class (field 3) | mark reordering (`CanonicalCombiningClass`) |
| `17.0.0/ArabicShaping.txt` | Joining_Type (field 2) | Arabic joining (`Joining`) |
| `17.0.0/BidiMirroring.txt` | Bidi_Mirroring_Glyph | RTL mirroring (`BidiMirroring`) |

Later stages add `Scripts.txt` (H5 itemization) and `BidiBrackets.txt` / `Bidi_Class`
(H6 UAX #9) to this snapshot.

## Regenerate

From the repo root, after editing the tool or bumping the snapshot:

```
dotnet run --project tools/UcdGen
```

Output is deterministic (same input → byte-identical `*.g.cs`), so a no-op run produces no
diff. Each generated file carries a provenance header naming the source file and its SHA-256.

## License

`LICENSE.txt` is the Unicode license (UNICODE LICENSE V3) governing these data files.
