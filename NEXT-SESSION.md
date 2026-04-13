# Next session — Phase 8 follow-up

Handoff notes after the session that landed Phases 1–9 + 12 + a Phase 8
foundation. Work to do here is bounded: finish Phase 8 properly, then
re-validate DIR.Lib's baselines against hinted output.

## Current state

```
Fonts.Lib HEAD:
  ec5b9c6  Hinting: replace try/catch with FreeType-style underflow handling
  3940...  Phase 8 foundation: TT bytecode interpreter (incomplete)
  5ef29c5  ROADMAP: Phase 8 active, Phase 9 conditional
  ceea8e8  Add TODO.md
  3c2d59c  Fix DrawGlyph bypassing variation
  ...     (Phases 1–9 + COLR + CBDT + Variable + Type 1)

DIR.Lib HEAD:
  …       Phase 12: full FT removal + v2.0.0 bump
  …       Phase 12 (conservative): RgbaImageRenderer → ManagedFontRasterizer
```

**Test counts:** Fonts.Lib 126/126; DIR.Lib was 87/87 immediately after
the conservative swap, then 16 of those failed when FT was fully removed
(all in `RenderAcceptanceTests.CompareBaseline*`) — see "Why baselines
don't match yet" below.

## What's done in Phase 8

- `Tables/Maxp` reads v1.0 hinting fields (maxStorage, maxFunctionDefs,
  maxStackElements, maxTwilightPoints, etc.).
- `Tables/{Cvt2, Fpgm2, Prep2}` table tags + parsing
  (`Cvt` is `ushort[]` of FUnit values; `Fpgm`/`Prep` are raw `byte[]`).
- `Hinting/F26Dot6` — 26.6 fixed-point arithmetic helpers.
- `Hinting/Zone` — point storage (twilight + glyph) with current+original
  coords + touched flags.
- `Hinting/GraphicsState` — TT VM "registers" (round state, reference
  points, zone pointers, projection / freedom / dual vectors, cut-ins, etc.).
- `Hinting/Opcode` — full opcode enum (~150 entries).
- `Hinting/PopPushCount` — per-opcode (pop, push) table ported verbatim
  from `freetype/src/truetype/ttinterp.c` `Pop_Push_Count[256]`.
- `Hinting/Interpreter`:
  - Operand stack, dispatch loop, function table.
  - **~60 opcodes implemented**: stack/arithmetic/logic/storage/CVT/
    round-modes/graphics-state-setters/control-flow/functions.
  - Pre-dispatch underflow handling (zero-fills missing args, FT-style
    non-pedantic mode).
  - `(uint)i < (uint)len` bounds checks on storage/CVT indices so
    negative or oversized indices silently no-op.
  - Per-instruction range checks on MINDEX/CINDEX/ROLL.
- `OpenTypeFont.HasHinting` + `CreateHintingInterpreter()` — instantiates
  + runs `fpgm` once.
- `HintingFoundationTests.cs` — 4 smoke tests verifying parse + dispatch
  + multi-size `prep` runs across the corpus without throwing.

## What's NOT done (the actual work)

The interpreter runs without crashing, but the **hinting verbs are still
no-ops**, so glyph point coordinates are never moved by the hint program.
Output remains unhinted.

### Wiring

- [ ] **Wire glyph instructions into `Glyf.LoadGlyph`**
  - Read the `instructionLength` + instruction bytes from each glyph's
    header (already skipped at `Outlines/SimpleGlyphParser.cs:36-37`).
  - Build a `Zone` from the loaded outline + 4 phantom points (lsb,
    advance, top side bearing, bottom side bearing).
  - Run `Interpreter.RunGlyphProgram(instructions, zone)`.
  - Convert F26.6 zone coords back to short[] for the returned `Outline`.
  - For composite glyphs, hint each component then assemble (or — easier
    — apply composite glyph's own instructions over the assembled outline,
    per spec §"Composite glyph hinting").
  - Need a `prep`-was-run flag per ppem so `OnSizeChange` is only invoked
    when ppem actually changes (cache CVT-after-prep).
  - **~80 LOC.**

- [ ] **Hook into `OpenTypeFont.LoadGlyphOutline`**
  - Maintain a thread-local or per-call interpreter snapshot (interpreter
    has mutable per-glyph state; lock-free design forbids sharing one
    instance across threads).
  - Easiest: factor `Interpreter` so per-face state (functions, storage,
    CVT) is on a `HintingFace` immutable object; per-glyph state
    (stack, zones, GS) lives in a per-call `HintingExec` struct that
    references the face.
  - Adds a hint-or-not toggle on `OpenTypeFont` so callers can opt out
    (useful for measuring/layout where hinting is wrong).

### Hinting verbs — the actual work

Estimated LOC against FreeType's `ttinterp.c` (use as reference but
**re-implement, don't copy** — FreeType's FTL license is permissive
but mixing requires NOTICE preservation; cleaner to write fresh).

- [ ] **MDAP[a]** (0x2E–0x2F, ~50 LOC) — Move Direct Absolute Point.
  Snap a single point to the pixel grid along the freedom vector.
  Sets rp0/rp1.
- [ ] **MIAP[a]** (0x3E–0x3F, ~80 LOC) — Move Indirect Absolute Point.
  Snap to a CVT value; if the difference exceeds `cvtCutIn`, use the
  point's original position rounded.
- [ ] **MDRP[abcde]** (0xC0–0xDF, 32 variants, ~150 LOC) — Move Direct
  Relative Point. Move along freedom vector by the original distance
  between rp0 and the target (rounded per a/b/c/d/e flag bits).
- [ ] **MIRP[abcde]** (0xE0–0xFF, 32 variants, ~200 LOC) — Move Indirect
  Relative Point. Same as MDRP but distance comes from CVT.
- [ ] **IUP[xy]** (0x30–0x31, ~120 LOC) — Interpolate Untouched Points.
  After explicit hints, smooth out the rest of the contour by linear
  interpolation between touched points along x or y axis. Critical for
  curve glyphs.
- [ ] **ALIGNRP** (0x3C, ~30 LOC) — Align Relative Point. Move target
  point to lie on rp0 along projection vector.
- [ ] **IP** (0x39, ~50 LOC) — Interpolate Point. Like IUP but for an
  explicitly-listed loop of points using rp1/rp2 as anchors.
- [ ] **SHP / SHPIX / SHC / SHZ** (0x32–0x38, ~100 LOC) — Shift Point /
  Shift by Pixel / Shift Contour / Shift Zone. Bulk-move points by a
  delta.
- [ ] **DELTAP1/2/3** (0x5D, 0x71, 0x72, ~80 LOC) — apply per-ppem
  point fixups (small adjustments at specific pixel sizes).
- [ ] **DELTAC1/2/3** (0x73–0x75, ~60 LOC) — same for CVT entries.

### Round modes

The setters (RTG/RTHG/RTDG/RDTG/RUTG/ROFF) are already wired but the
rounding **function** is not (`Round(distance) → distance`). MDAP /
MIAP / MDRP / MIRP all need it.

- [ ] **`Round(int distance)` helper** — implements the period/phase/
  threshold quantization from `_gs.RoundState`. ~40 LOC. Reference:
  FT's `Round_None`, `Round_To_Half_Grid`, etc. in `ttinterp.c`.
- [ ] **SROUND / S45ROUND** (0x76, 0x77) — set custom round period
  from a single byte argument. ~30 LOC.

### Misc opcodes still missing

- [ ] **MD** (Measure Distance, 0x49–0x4A) — used by hint programs to
  query the current distance between two points.
- [ ] **GC** (Get Coordinate, 0x46–0x47) — query a point's coordinate
  along the projection vector.
- [ ] **SCFS** (Set Coordinate From Stack, 0x48) — write a point's
  coordinate.
- [ ] **ALIGNPTS** (0x27) — align two points to their midpoint.
- [ ] **ISECT** (0x0F) — line intersection.
- [ ] **UTP** (0x29) — clear the touched flag on a point.
- [ ] **FLIPPT / FLIPRGON / FLIPRGOFF** (0x80–0x82) — toggle on-curve
  flags (rarely needed).
- [ ] **INSTCTRL** (0x8E) — instruction control (turn off hinting at
  small sizes, etc.).

### Cleanup

- [ ] **Remove the `_glyph` null-forgiveness** (`= null!`) once
  `RunGlyphProgram` actually uses it.
- [ ] **Add a `pedantic` mode flag** that opts into FT's strict-error
  behavior — useful for testing / debugging.

## Validation

After the verbs land:

1. **DejaVu Sans 'H' cap-height snap** — the test that prompted this
   work. Render `H` at 24px, expect bitmap top at row 10 (matches FT
   baseline) instead of row 11 (current unhinted result).
2. **Re-run DIR.Lib's `RenderAcceptanceTests`** — 16 currently-failing
   baseline-image tests should drop to zero or near-zero failures.
   Anything still failing is a real bug to chase (or a real legitimate
   visual improvement from our newer rasterizer, in which case
   regenerate THAT specific baseline with `DIR_LIB_UPDATE_BASELINES=1`).
3. **Ship Phase 8** — bump version, update ROADMAP / TODO to mark
   complete.

## References

Local source available for cross-reference (NOT for copying — port
algorithmically):

- `C:/Users/SebastianGodelet/source/repos/other/freetype/src/truetype/ttinterp.c`
  — the canonical TrueType interpreter. Particularly:
  - L409–660 `Pop_Push_Count[256]` (already ported)
  - L2477–6800 individual opcode implementations (`Ins_*`)
  - L6770–6810 main dispatch loop with underflow handling (already ported)
  - L1320–1450 `TT_RunIns` outer entry (good model for our
    `RunGlyphProgram`)

Spec links:

- Microsoft OpenType TT instruction set:
  https://learn.microsoft.com/typography/opentype/spec/tt_instructions
- Apple TrueType reference manual: https://developer.apple.com/fonts/TrueType-Reference-Manual/

## Test commands

```bash
# Fonts.Lib full suite
cd C:/Users/SebastianGodelet/source/repos/sharpastro/Fonts.Lib
dotnet test SharpAstro.Fonts.slnx

# Just hinting tests
dotnet test SharpAstro.Fonts.slnx --filter "FullyQualifiedName~Hinting"

# DIR.Lib full suite (after Fonts.Lib changes propagate via ProjectReference)
cd C:/Users/SebastianGodelet/source/repos/sharpastro/DIR.Lib
dotnet test src/DIR.Lib.Tests/DIR.Lib.Tests.csproj

# Just the baseline-image tests that should improve as hinting lands
dotnet test src/DIR.Lib.Tests/DIR.Lib.Tests.csproj --filter "FullyQualifiedName~RenderAcceptance"
```

## Constraints

Reminders the user has flagged in past sessions (also in `~/.claude/.../memory/`):

- **Don't propose `DIR_LIB_UPDATE_BASELINES=1` as a first response to a
  baseline failure.** Investigate root cause first; only regenerate if
  the new output is genuinely more correct than the old.
- **Memory conservation + lock-free thread safety** are non-negotiable.
  The interpreter's per-glyph state has to either live on the calling
  stack (struct-passed) or in a per-call snapshot — never share a
  mutable interpreter across threads.
- **Merida is chess-pieces-only.** Don't add Latin-glyph tests against it.
- **MIT only.** Algorithmic ports of FT are fine; literal copies are not.
