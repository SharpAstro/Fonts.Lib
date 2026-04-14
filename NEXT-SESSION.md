# Next session — Phase 8.5 follow-up

The previous session landed the Phase 8 core: glyph-program wire-through
(`OpenTypeFont.LoadHintedOutline` / `RenderGlyphHinted`), the round-mode
machinery (incl. SROUND/S45ROUND), projection / freedom / dual-vector
arithmetic, and the common-path verbs (MDAP, MIAP, MDRP, MIRP, ALIGNRP,
ALIGNPTS, IP, IUP[xy], SHP, SHC, SHZ, SHPIX, MSIRP, UTP, GC, MD, SCFS,
FLIPPT, FLIPRGON, FLIPRGOFF, INSTCTRL).

**Test counts:** Fonts.Lib 131/131 (was 126/126 + 5 new hinting tests).
The new tests verify that hinting produces non-empty hinted outlines,
that distinct X coords land on (or very near) integer pixel boundaries
after MDRP/MIRP-driven snapping, and that hinting materially shifts
points from their naively-scaled positions.

## Phase 8.5 — what's left

### Verbs not yet implemented

- [ ] **DELTAP1 / DELTAP2 / DELTAP3** (0x5D, 0x71, 0x72) — per-ppem
      point deltas (small adjustments at specific pixel sizes). Used
      heavily by Microsoft fonts (Verdana, Tahoma, Segoe UI). Without
      these, those fonts won't match FT pixel-perfectly at small sizes.
- [ ] **DELTAC1 / DELTAC2 / DELTAC3** (0x73, 0x74, 0x75) — same but
      patches the CVT entries.
- [ ] **ISECT** (0x0F) — line-line intersection. Rarely emitted.

### Architecture

- [ ] **Per-(face, ppem) cache.** `HintingPipeline.Run` currently builds
      a fresh `Interpreter` and re-runs `fpgm` + `prep` on every glyph
      render. For real workloads (e.g. rendering an entire string at
      one ppem) this is wasteful. Cache: keyed by `(OpenTypeFont, ppem)`,
      stores post-prep CVT[], post-fpgm function table snapshot, and
      end-of-prep storage[] / GS. Per-glyph render then clones the
      mutable bits into a per-call snapshot.
- [ ] **Lock-free per-call interpreter snapshot.** `OpenTypeFont` is
      documented as lock-free thread-safe (immutable post-construction).
      Today the hinting path violates that — `Interpreter` mutates
      `_storage`, `_stack`, `_gs`, etc., and the same instance is
      returned from `CreateHintingInterpreter()` per call. Concurrent
      renders would corrupt state. Fix: split into immutable
      `HintingFace` (functions, raw CVT, post-fpgm storage) + per-call
      `HintingExec` struct passed by ref through the dispatch loop.
- [ ] **Engine compensation / cut-in by distance type.** MDRP/MIRP
      decode bits 0-1 of the opcode as the "color" (gray / black /
      white). FT applies a per-color compensation to the rounded
      distance (see `Engine_Compensation` in `ttinterp.c`). Currently
      ignored — fine for grayscale at modern ppem but causes ~1/64 px
      drift vs. FT in some stems.

### Validation work for the downstream consumer

The user's own `DIR.Lib` was reported as having 16 baseline-image
test failures after FT was fully removed (Phase 12). With Phase 8 core
landed those should drop substantially. Suggested workflow:

```bash
# Re-run DIR.Lib's render acceptance tests against the new hinted path.
cd C:/Users/SebastianGodelet/source/repos/sharpastro/DIR.Lib
dotnet test src/DIR.Lib.Tests/DIR.Lib.Tests.csproj \
  --filter "FullyQualifiedName~RenderAcceptance"
```

If a baseline still fails:
1. Diff the bitmaps; identify which glyph(s) differ.
2. If the difference is one of the unimplemented verbs (DELTAP/DELTAC
   most likely for Verdana / Tahoma / Segoe), implement that verb.
3. Only as a last resort regenerate the baseline with
   `DIR_LIB_UPDATE_BASELINES=1`, and only when the new output is
   genuinely more correct.

## Known gotchas

- `Interpreter.SetGlyphContours` is called by `HintingPipeline` before
  `RunGlyphProgram`. IUP and SHC silently no-op without it. If you
  add a code path that calls `RunGlyphProgram` directly, set
  contour ends first or those verbs become no-ops.
- The `_glyph` field defaults to `new Zone(0)` (empty placeholder) so
  prep programs that incidentally touch glyph-zone state during size
  change don't NPE. Replace this if you refactor for thread safety.
- `MovePoint` skips when `proj·free == 0` (vectors perpendicular).
  This matches FT's behavior but means malformed hint programs that
  set incompatible vectors silently produce no movement.

## References

- `C:/Users/SebastianGodelet/source/repos/other/freetype/src/truetype/ttinterp.c`
  — keep handy for delta-P/C, ISECT, and the engine compensation table.
- Microsoft TT instruction reference:
  https://learn.microsoft.com/typography/opentype/spec/tt_instructions
