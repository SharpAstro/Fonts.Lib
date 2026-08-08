# Web Showcase — specimen page + in-browser font inspector

Status: **PROPOSED** (2026-08-08). Nothing built. This doc exists to decide scope before code.

Goal: a static GitHub Pages site for `SharpAstro.Fonts` shaped like a **Google Fonts specimen
page** — type your own text, drag a size slider, browse the glyph set, pull variable-font axes —
with the twist that **every preview renders twice, side by side: the browser's rasterizer via
`@font-face` on the left, ours on the right, from the same bytes.** Plus a drop-target that loads
any font the visitor has and shows what is inside it.

Deploy target: `https://sharpastro.github.io/Fonts.Lib/`.

Mirrors the pattern already shipped in the sibling `tianwen` repo
(`tianwen/.github/workflows/pages.yml`, `src/TianWen.UI.Web`, `docs/plans/web-showcase.md`) —
Blazor WebAssembly, fully static, no server.

## Why this is feasible — and an easier port than tianwen's

The library is a near-ideal WASM candidate, and most of what tianwen's Pages workflow fights with
simply does not exist here:

| tianwen's problem | Fonts.Lib |
|---|---|
| Three P/Invoke-dense vendor SDKs crash the mono AOT cross-compiler; worked around with `_AOT_InternalForceInterpretAssemblies` | No `DllImport`/`LibraryImport`/`NativeLibrary` anywhere in `src/`. `AllowUnsafeBlocks=false`, `IsAotCompatible=true` |
| 30 MB Tycho-2 catalog embedded in the library, stripped via a global `-p:Lightweight=true` property | No embedded data of any kind |
| git-LFS pull ordering; a stale pointer failed the first deploy | No LFS in this repo |
| JPL SBDB sends no CORS headers, so a comet fetch is permanently impossible from a browser origin; baked in CI instead | No network dependency at all |
| `BrowserExternal` shim needed — desktop `IExternal` bundles serial ports, TCP sockets, atomic file writes | `LoadFromFile` is a thin convenience wrapper; every loader has a `ReadOnlyMemory<byte>` / `ReadOnlySpan<byte>` overload that takes uploaded bytes directly |

The only dependency is `SharpAstro.Png` (CBDT/sbix colour-bitmap decode), itself pure managed.

Nothing in the render path needs a server, so an uploaded font never leaves the visitor's machine —
worth stating on the page itself, since people are justifiably wary of uploading licensed fonts to
a web service.

## The comparison — what it is, and what it must not become

Owner's call (2026-08-08): **render text with a webfont and ours, side by side.** That is a
demo for a human to look at. It is deliberately *not* an automated pixel-diff, and the plan should
keep it that way.

**What it is.** The same uploaded `ArrayBuffer` drives both sides — `new FontFace(name, buffer)` +
`document.fonts.add()` for the browser, `OpenTypeFont.Load(bytes)` for us — so there is no chance
of the two halves disagreeing about which file they are showing. Same string, same px size, same
baseline origin. Controls that make it informative rather than decorative:

- **Size slider**, because the interesting divergence is at small ppem where hinting bites.
- **Hinting on/off** (`RenderGlyph` vs `RenderGlyphHinted`) — our side can show its own work.
- **Onion-skin / wipe overlay**, so the two can be superimposed rather than only juxtaposed.

**What it must not become.** A pass/fail pixel assertion. The browser is not ground truth for
rasterization: Chrome, Firefox and Safari each apply their own hinting, gamma correction, stem
darkening and subpixel positioning, and they differ across platforms for the same font at the same
size. A pixel diff would light up with differences that are not defects, and would end up either
permanently red or tolerance-fudged until it asserts nothing. Keep it in the showcase, out of CI.

Related limit, worth knowing before someone proposes it: **no web API exposes glyph outlines**, so
paths cannot be diffed against the browser at all — only rendered pixels and metrics.

**The one part that *is* a real oracle** — deferred, see P5. `ctx.measureText()` on a `FontFace`
built from the same bytes yields advance widths and bounding boxes, and that exercises
cmap → glyph id → `hmtx` → `kern`/GPOS as one chain against an outside authority. Nothing in the
current suite checks that path against a third party. It is cheap once the harness exists, and it
is the only browser comparison that can legitimately be an assertion.

## Bundled fonts, payload, and licensing

This is the sharpest constraint on the design, and the one place a mistake would be a licensing
problem rather than a bug.

**The fixture corpus cannot be shipped wholesale.** `tests/SharpAstro.Fonts.Tests/Fixtures/` is
41 MB, and two distinct reasons rule most of it out:

*Licensing.* A public site **redistributes** everything it serves. These fixtures must **not** be
published:

| Fixture | Why not |
|---|---|
| `XXTIIT_Arial_subset.ttf` | Monotype Arial, subset out of a PDF |
| `Tahoma_subset.ttf` | Microsoft Tahoma |
| `D011A_subset.ttf` | Canon EOS450D manual embedded subset |
| `LithosBold_subset.cff` | Adobe Lithos, subset out of a PDF |
| `ISOCPEUR_subset.ttf` | Autodesk/Monotype ISOCPEUR |
| `Merida.ttf` | Licence not established — resolve before shipping, or drop |

Believed fine to ship, **with `OFL.txt` and attribution alongside**: the Noto family
(`NotoSans*`, `Noto-COLRv1`, `NotoColorEmoji`), `SourceSans3-Regular.otf` and `RobotoFlex.ttf` are
SIL OFL 1.1; `DejaVuSans.ttf` is under the DejaVu licence (Bitstream Vera derivative, free to
redistribute); `cmr10.pfb` is Computer Modern (Knuth/AMS, freely redistributable);
`BabelStoneXiangqiColour.ttf` is believed OFL. Our own `SharpAstroTest-{unifont,bigfont}.shx` are
MIT — we authored them.

**Confirm rather than assume, before the first deploy.** The above is from recollection of these
projects' usual terms, which is not good enough for something we publish. The authoritative source
travels inside each file: `name` IDs 13 (License Description) and 14 (License URL). We can read
those with our own library — `font.Name` is already public — so P4 should include a small pass that
dumps ID 13/14 for every candidate and gates the shippable set on the result. Pleasingly, that
makes the licence audit a dogfooding exercise rather than a chore.

*Payload.* Owner picked the Noto family as the demo set, which is right for a Google-Fonts-shaped
page, but they are the big ones: `NotoColorEmoji.ttf` alone is 10.4 MB, `NotoSansSC` 8.1 MB,
`Noto-COLRv1` 4.9 MB. **None of these go into the WASM bundle.** They ship as ordinary static
assets under `wwwroot/fonts/` and are `fetch`ed on demand when the visitor selects that specimen —
the browser HTTP-caches them, and a visitor who never opens the emoji specimen never pays for it.
This is exactly the trick tianwen used to keep `tyc2.bin.lz` out of its bundle.

Only the landing specimen is fetched eagerly. Candidate: `NotoSans-Regular` (~450 KB, OFL, not
currently in the repo — would be added for the site) or `SourceSans3-Regular.otf` (327 KB, already
present).

The emoji and COLRv1 faces earn their weight as lazy fetches: they are the CBDT and COLR v1
showcases, and colour glyphs rendered by our own decoder are the most visually convincing thing
the library does.

## What to showcase — lead with what browsers cannot do

A side-by-side where we merely match the browser proves we are an also-ran. The material that
justifies the site is the material with no left-hand column at all:

- **AutoCAD SHX** (shipped in 1.9). No browser renders it. The hook writes itself: SHX text in a
  plotted PDF arrives as bare path geometry with no font object and no `/ToUnicode`, which makes it
  invisible to every text extractor. Fixtures are 0.3 KB combined — free to ship, and ours.
- **PostScript Type 1 `.pfb`** — browsers dropped Type 1 support entirely. `cmr10.pfb` is 
  recognisable to anyone who has read a paper set in TeX.
- **The inspector.** Table directory, which `cmap` subtable format, the COLR v1 paint tree, `fvar`
  axes, `MATH` constants, whether `fpgm`/`prep` are present. The public API already surfaces all of
  it (`Directory`, `Cmap`, `Colr`, `Fvar`, `Math`, `Name`, `Os2`, `HasHinting`, `IsVariable`,
  `TryGetTable`). Prior art to aim at: wakamaifondue.com, fontdrop.info.
- **Hinting on/off at 11 ppem**, and the SDF/MTSDF outputs (`RenderSdf`, `RenderMtsdf`) — neither
  has any browser equivalent.

## Shaping — decided: in (owner, 2026-08-08)

The specimen page lays text out through `SharpAstro.Fonts.Shaping`, not naive cmap + `hmtx`.

The cost objection I raised against this evaporates on measurement: `SharpAstro.Fonts.Shaping.dll`
is **95.5 KB** against the core's 302.5 KB — smaller than every font it will shape, already
`IsAotCompatible`, `AllowUnsafeBlocks=false`, and its only reference is the core library. There is
no reason not to.

The API is a clean fit for a live preview box: `ScriptItemizer.Itemize(text)` (or
`BidiScriptItemizer.Itemize` when direction matters) splits arbitrary typed text into
`ScriptRun`s by itself, `ShapingFont.Create(font)` wraps the face, and `Shaper.Shape` fills a
`ShapeBuffer` exposing `GlyphIds`, `Clusters`, `XAdvanceDeltas`, `XOffsets` and `YOffsets` — which
is exactly the input a canvas blit loop wants. **BiDi is present too** (`BidiAlgorithm.Resolve` /
`Reorder`), so RTL is a real specimen rather than a mangled one.

**The scope boundary must be visible on the page.** There are two shapers, `ArabicShaper` and
`DefaultShaper`; the package description states the position plainly — OT-layout core,
ligatures/kerning/marks and Arabic joining are in, **Indic and USE are out of scope**. So Devanagari
or Khmer typed into the preview box will render worse than the browser's half, and the site would
be advertising a deliberate scope decision as though it were a defect. Fix: the specimen page
itemizes the text anyway (it gets `ScriptRun`s for free) and labels each run *supported* or
*out of scope*, turning the failure into an honest, live coverage map. That is a better page than
one which quietly only ever shows Latin.

**Shaped text does not make the browser a better oracle — the opposite.** Chrome and Firefox both
shape with HarfBuzz, so a shaped side-by-side is really us versus HarfBuzz, and this repo *already*
runs that comparison offline with better controls: `tools/HbFixtureGen` generates golden fixtures
from real HarfBuzz via `HarfBuzzSharp`. The browser adds nothing there that the goldens do not
already cover. It remains an excellent *demo* — ligatures forming and Arabic letters joining as you
type is the most legible proof the engine works — which is another reason the side-by-side belongs
in the showcase and not in CI. It also keeps P6 correctly scoped: the oracle worth automating is
**metrics**, not shaping.

## Project shape

New `src/SharpAstro.Fonts.Web` (`Microsoft.NET.Sdk.BlazorWebAssembly`, net10.0),
`IsPackable=false`, `InvariantGlobalization=true`.

**Under Central Package Management from day one, and inside `SharpAstro.Fonts.slnx`.** Both of
tianwen's web projects opted out of CPM as though it followed from being outside the solution. It
does not — CPM resolves by walking directories and has no bearing on solution membership — and the
opt-out is precisely what let `WebGl.Renderer` drift two minors behind and `Microsoft.NET.Test.Sdk`
sit at 18.6.0 against a central 18.3.0, both invisible to a sweep of the props file. Their csproj
comments now say so at length. Inherit the lesson, not the mistake.

Rendering path: `GlyphBitmap` is `{ byte[] Alpha, int Width, int Height, int Left, int Top }`,
which maps onto a canvas `ImageData` with no intermediate representation — expand the 8-bit alpha
to RGBA and `putImageData`. Colour glyphs come back from `RenderColor` as `ColorBitmap`. No WebGL,
no shader work, no renderer sibling dependency: this is a 2D canvas app.

Open item: the library is `IsAotCompatible` but **not** marked `IsTrimmable`. Either add it (and
verify) or root it via `TrimmerRootAssembly` — decide during P0.

## Phasing

| Phase | Scope | Risk | Ships |
|---|---|---|---|
| **P0** | Blazor WASM shell, canvas host, `GlyphBitmap` → `ImageData` blit, one hard-coded font. Trimming decision. | Low | — |
| **P1** | **Specimen page**: type-your-own-text box, size slider, glyph grid, `fvar` axis sliders via `WithVariation`. Google-Fonts-shaped. Layout runs through **`SharpAstro.Fonts.Shaping`** — `ScriptItemizer` → `Shaper.Shape` → blit from `ShapeBuffer`, with BiDi for RTL and per-run in-scope/out-of-scope labelling. | Low-Med | The demo |
| **P2** | **Side-by-side**: `FontFace` from the same bytes, browser left / ours right, hinting toggle, onion-skin wipe. | Low | The headline |
| **P3** | **Upload + inspect**: drop target, table directory, cmap format, COLR v1 paint tree, name/OS2, hinting presence. | Med | The inspector |
| **P4** | **SHX + Type 1 panel** — the no-browser-equivalent column. Plus lazy-fetch specimens for the big Noto faces. | Low | Differentiation |
| **P5** | **Deploy**: `pages.yml` forked from tianwen's, minus LFS/bake/Lightweight. Enable Pages with `build_type=workflow`. | Low | Live site |
| **P6** *(optional)* | **Metrics oracle**: Playwright + xunit.v3 harness comparing `measureText()` advances against ours across the OFL fixtures. Mirrors `TianWen.UI.Web.E2E`. Outside the default `dotnet test` sweep. | Med | A real test |

Incremental value: **P0 → P1 → P2 → P5** is a live, self-justifying site. P3/P4 deepen it. P6 is
the only phase that adds test coverage rather than demo surface, and is independent of the rest.

## Gotchas inherited from tianwen — measured there, not here

Every one of these cost that repo a debugging session. They are properties of Blazor WASM on
GitHub Pages, not of TianWen, so they will bite here identically.

- **WASM AOT is the deploy recipe, not an optimisation.** Interpreted vs `RunAOTCompilation=true`,
  measured A/B in-browser: 13.6 s → 554 ms init (24×), 24.9 s → 591 ms sweep (42×). Payload cost
  16 → 21 MB brotli. Our rasterizer and the v40 hinting interpreter are exactly the kind of tight
  scalar loop that gets murdered by the interpreter — **assume AOT is required and measure early**,
  because it changes the deploy job (needs `dotnet workload install wasm-tools` on ubuntu).
- **`index.html` must carry the `OverrideHtmlAssetPlaceholders` markers** — the `webassembly`
  preload link, an empty `<script type="importmap">`, and
  `_framework/blazor.webassembly#[.{fingerprint}].js`. Without them the *published* page 404s,
  because only fingerprinted asset names exist in `_framework`. **The dev server tolerates plain
  names, so this only ever bites on deploy.**
- **The browser is single-threaded: `Task.Run` queues on the UI thread.** tianwen's "background"
  catalog decode wedged the page. Our equivalent is rasterizing a full glyph grid for a CJK face —
  `NotoSansSC` has thousands of glyphs. Needs explicit yields (`StateHasChanged` + `Task.Delay`)
  and probably windowed/lazy grid rendering.
- **Blazor's `firstRender` fires during the first `await` in `OnInitializedAsync`**, so an
  `if (firstRender && ready)` guard never fires. Paint explicitly after init.
- **Pages plumbing**: rewrite `<base href="/" />` to `/Fonts.Lib/` on the published copy only;
  `touch .nojekyll` (Jekyll strips `_framework/` for the leading underscore); `cp index.html
  404.html` for client-side deep links.
- **Pages must be enabled once** with build type `workflow` — Settings → Pages → Source: GitHub
  Actions, or `gh api -X POST repos/SharpAstro/Fonts.Lib/pages -f build_type=workflow`.

## Open questions

1. **Does the site build gate the release?** `dotnet.yml` currently publishes both packages on
   push to main. A separate `pages.yml` with a `paths:` filter keeps them independent — recommended,
   and what tianwen does.
2. **Landing font**: add `NotoSans-Regular.ttf` for the site, or reuse `SourceSans3-Regular.otf`
   which is already in the repo and half the size?
3. **`Merida.ttf` licence** — establish it or drop it from the shippable set.
4. ~~Shaping satellite in v1?~~ **Decided in: see "Shaping" above.**
5. **Arabic specimen font.** Shaping is in and `ArabicShaper` is the marquee non-Latin case, but no
   Arabic face is currently in the fixtures — the Noto set here is CJK plus Latin. Ships as an
   added OFL asset (Noto Sans Arabic), or Arabic joining goes undemonstrated on the very page
   built to show it off.
