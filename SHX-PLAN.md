# Plan — AutoCAD SHX shape/font support

> **Status: implemented (phases 1–6).** `src/SharpAstro.Fonts/Shx/`, with `ShxTests` on the
> bundled fixtures and `ShxRealFaceTests` as an opt-in breadth suite. Phase 7 (`shapes`) is
> still not done and still optional.
>
> Three things in this document were **wrong**, and were corrected against a 4,428-file corpus
> during implementation. They are fixed in place below, and flagged where they sit so a reader
> who remembers the original is not misled:
>
> * the **bigfont record layout** — it is an index table, not inline records;
> * the **header length** — it is not fixed at 25 bytes;
> * the **`0x07` operand** — its size and endianness differ per format, and bigfont has an
>   escape form this plan did not mention.
>
> The measured facts that settled them are stated inline. Where something is still inference
> rather than specification, it says so.

## Why

AutoCAD's `.shx` faces are the last common font format in AEC drawings that no managed library
reads. When a DWG is plotted to PDF with an SHX text style, the glyphs are emitted as **path
geometry** — no font object, no `/ToUnicode`, nothing for a text extractor to find. A sheet can
carry thousands of characters that are invisible to every consumer.

Measured on a corpus of 8,590 production drawing sheets (Drawboard Projects):

| | |
|---|---|
| sheets with under 50 extractable characters | 1,634 (19.0%) |
| of those, no `AutoCAD SHX Text` comment annotation and no `Tr 3` OCR layer | **1,541** |
| SHX characters recoverable on sheets that *do* carry comment annotations | 3,455,220 |
| of those, with no other text source behind them | **96.8%** |

Two of the 1,541 were rendered at random and are not scans: a mechanical-services drawing register
(sheet index, title block, drawing number) and a bilingual fire-protection sheet — dense vector
line-work in which every character is stroked geometry.

Separately, a matching experiment establishes that **the font files are the answer**. Glyphs
extracted from real PDFs were matched by nearest-neighbour against templates decoded from the stock
faces: **69.0% top-1, 76.6% top-3**, with no training and a naive 16×16 raster comparison. Matching
against a single face gave 19.1%; the corpus turned out to be mostly `romans` (42%) and `isocp`
(35%), with `txt` at 4%. Which face was used is recorded nowhere in the PDF, so the bank has to hold
every candidate and the winning match is what identifies the font.

That makes `.shx` decoding a font-library concern rather than an application one, and this library
is the natural home: it already reads OpenType, CFF, Type 1, WOFF and WOFF2, and already contains
two byte-program glyph interpreters that SHX closely resembles.

## The three formats

The header is ASCII terminated by `0x1A`. Its text selects the layout.

**It is not a fixed 25 bytes** — that holds for unifont and bigfont but not in general: `shapes`
headers are 24, one surveyed file uses a bare `\n` and is 23, and one bigfont calls itself
`AutoCAD-586` and is 26. Scan for the terminator rather than seeking to a fixed offset. Match the
layout word case-insensitively too; `AutoCAD-86 Shapes 1.0` occurs.

| header | files | keyed by | in scope |
|---|---|---|---|
| `AutoCAD-86 unifont 1.0` | `txt`, `romans`, `romand`, `romanc`, `isocp`, `monotxt`, `italic`, `gothice`, `complex` | **Unicode code point** | **yes** |
| `AutoCAD-86 bigfont 1.0` | `gbcbig`, `chineset`, `extfont`, `whgtxt`, `bigfont` | double-byte CJK code | **yes** |
| `AutoCAD-86 shapes 1.0` / `1.1` | `simplex`, `ACAD.SHX`, symbol libraries | shape *number* | no — see below |

`shapes` files are **not text fonts**. They are symbol libraries used for linetype decorations,
P&ID marks, weld symbols and survey markers, addressed by shape number from a DWG rather than by
character. They should be rejected with a clear error, not read with the wrong layout: `simplex.shx`
parsed as `unifont` yields three nonsense glyphs and then crashes the interpreter on an
out-of-range index. Ask for `shapes` support only if a caller needs symbol lookup by number.

### unifont layout

Records are stored **inline**, one after another:

```
byte[]    "AutoCAD-86 unifont 1.0\r\n\x1a"
uint32    glyph count (includes the font-definition record)
uint16    font-definition length
byte[]    font name, NUL-terminated, then above, below, modes, ...
repeated: uint16 code, uint16 length, byte[length] data
```

`above` / `below` are the ascent and descent in font units — the em is `above + below`, and there is
no `unitsPerEm` field. `modes` non-zero means the face has vertical variants (see `0x0E`).

154 of 170 surveyed faces tile exactly to EOF. The other 16 carry a 48-byte ASCII GUID watermark
appended by some authoring tool, so **stop after `count - 1` records and ignore trailing bytes**
rather than treating them as a short read.

### bigfont layout

> **Corrected.** This originally described inline records like unifont's. It is not: bigfont reaches
> its records through an **index table of file offsets**. Read with the unifont layout, 344 of 362
> surveyed bigfont faces run past EOF. The two containers cannot share a code path.

```
byte[]    "AutoCAD-86 bigfont 1.0\r\n\x1a"
uint16    8 in 350 of 362 surveyed faces, 0 in the other 12; purpose unconfirmed, and the
          index entry size is a fixed 8 bytes either way
uint16    index entry count (includes the code-0 font-definition entry)
uint16    range count
repeated: uint16 lead-byte range start, uint16 range end
repeated: uint16 code, uint16 length, uint32 file offset      <- the index, 8 bytes per entry
byte[]    the data area those offsets point into              (code 0 = font definition)
```

Each record's bytes are the same shape as unifont's — NUL-terminated name, then opcodes — only the
way you *find* them differs. In 358 of 362 faces the index table abuts the data area byte-for-byte,
which is the check worth asserting while writing the parser. The other 4 are damaged, with entry
offsets past EOF, so range-check and drop entries individually rather than failing the file.

The ranges are the lead bytes of the double-byte encoding, and they identify it:
`extfont.shx` declares `0x81-0x9F, 0xE0-0xEA, 0xFD-0xFE` (Shift-JIS), `chineset.shx` declares
`0x80-0xFF` (Big5/GBK). The font does not say *which* encoding, only which bytes lead — so mapping a
bigfont glyph to Unicode needs the ranges plus a codepage choice by the caller. Treat the code as
opaque at the API boundary and let the caller supply the mapping.

### The glyph opcode language

Both formats share it. Every record begins with a **NUL-terminated glyph name** (usually empty, so
a bare `0x00`) before the opcodes. An interpreter that starts at byte 0 reads that as end-of-shape
and silently returns an empty glyph for every character — this is the single easiest mistake to make
and it is not obvious, because the font loads fine and just draws nothing.

| code | operands | meaning |
|---|---|---|
| `0x00` | — | end of shape |
| `0x01` / `0x02` | — | pen down / pen up |
| `0x03` / `0x04` | 1 | divide / multiply vector length by the next byte |
| `0x05` / `0x06` | — | push / pop position |
| `0x07` | see below | subshape reference — **the operand differs per format** |
| `0x08` | 2 | signed XY displacement |
| `0x09` | 2n+2 | run of signed XY displacements, terminated by `(0,0)` |
| `0x0A` | 2 | octant arc: radius, then `±0SC` — start octant, octant count (`0` = **eight**, the full circle), sign = clockwise |
| `0x0B` | 5 | fractional arc: start/end octant offsets, high/low radius, `±0SC`. Here octant count `0` means **zero** — an arc inside a single octant. This is the one place the two arc opcodes disagree |
| `0x0C` / `0x0D` | 3 / 3n+2 | bulge arc / run of bulge arcs. The `0x0D` terminator is a `(0,0)` displacement carrying **no** bulge byte |
| `0x0E` | — | the next command applies to vertical text only — skip it in horizontal |
| other | — | packed vector: high nibble = length 1–15, low nibble = direction 0–15 |

**The pen starts DOWN.** The format does not announce this and getting it wrong silently drops the
first stroke of every glyph that draws before issuing a pen command — 4,869 records in the corpus do.
The corroborating signal is that pen-ups outnumber pen-downs by 50,202 across 44,332 records, about
one unmatched lift each, which is what a pen-down default plus a closing lift produces.

**`0x07` operands**, which the original of this document did not pin down:

* **unifont** — a 2-byte code, **high byte first**. Big-endian, unlike every length and count in the
  container. Of 3,185 references across 170 stock faces, 3,181 resolve to a code the font actually
  defines when read high byte first, against 9 the other way.
* **bigfont** — one byte, *unless* that byte is `0x00`, which introduces an extended composition
  form used to build a CJK glyph out of radicals:
  `0x07, 0x00, code_hi, code_lo, base_x, base_y, width, height` — 7 operand bytes.
  Reading it as a plain single byte throughout gets 94.8% of records landing on their terminating
  `0x00` and 56% of references resolving; honouring the escape gets 99.98% and 98.5%.

`width`/`height` are a box in font units of the same magnitude as `above`, not a fixed-point
fraction — the most common triples are (above 60, w 59, h 60) and (above 15, h 15) with the width
varying, i.e. full-height radicals of differing widths. Scaling by `width/above` × `height/above`
and restoring the parent pen afterwards is **inference from those statistics, not specification**.

A cheap structural check covers all of the above at once: walking a record's opcode stream under the
right operand lengths must land on the terminating `0x00` exactly at the record's last byte. It does
for 99.98% of records in both formats. Any systematic operand-length error shows up immediately.

The 16 directions are **not** unit vectors. The dominant axis is 1.0 and the other 0.5, which is
what makes SHX diagonals land on a lattice and stay crisp at small sizes:

```
0:( 1, 0)  1:( 1, .5)  2:( 1, 1)  3:( .5, 1)  4:( 0, 1)  5:(-.5, 1)  6:(-1, 1)  7:(-1, .5)
8:(-1, 0)  9:(-1,-.5) 10:(-1,-1) 11:(-.5,-1) 12:( 0,-1) 13:( .5,-1) 14:( 1,-1) 15:( 1,-.5)
```

**Arcs are load-bearing.** Skipping `0x0A`/`0x0B` renders every round glyph without its curves. In
the matching experiment this showed up as a perfectly bimodal per-class result — 100% on
`T Y + 7 :` and exactly 0% on `D O R P a c e g` — which is worth knowing as a diagnostic signature.
Note that `txt.shx` uses no arcs at all (its `D` is six straight segments), so testing only against
`txt` hides the bug entirely; `romans` and `isocp` need them.

For an octant arc the current point lies **on** the circle at the start angle, so the centre is back
along that radius. The format never states a centre.

## How it fits this library

Three existing pieces make this a peer feature rather than a special case:

- **`Type1CharstringInterpreter` / `Type2CharstringInterpreter`** already do exactly this shape of
  work — walk an opcode stream, maintain pen state and a position stack, emit path commands. An
  `ShxShapeInterpreter` belongs beside them.
- **`IGlyphSink`** (`MoveTo` / `LineTo` / `QuadTo` / `CubicTo` / `Close`) is the right emission
  target. Packed vectors and `0x08`/`0x09` become `LineTo`; octant and fractional arcs become
  `CubicTo` rather than the polyline approximation a quick implementation would reach for.
- **The Phase 10 stroker** is what turns a stroked glyph into a fillable outline.

### The one real design tension: stroke, not outline

Every format the library reads today produces **closed contours to fill**. SHX produces a **pen path
with a width** — the width comes from the graphics state (in PDF, the `w` operator), not from the
font. There is no notion of a filled counter; the bowl of an `O` is a stroked circle, not two
contours.

That has to surface in the API rather than be hidden:

- A face exposes `IsStroked`, and SHX glyph geometry is an **open path**. Handing it to a fill
  rasterizer without stroking produces recognisable but wrong glyphs (self-intersecting, no weight).
- Rendering goes through the stroker with a caller-supplied width, cap and join. Reasonable
  defaults: round cap, round join — AutoCAD plots SHX with a pen.
- Consumers that only want geometry (text extraction, shape matching, hit-testing) take the open
  path directly and skip the stroker. This is the primary use case driving the request.

### Metrics

There is no `unitsPerEm`, `hmtx` or kerning. Advance width is the pen position at end-of-shape;
ascent/descent come from the font-definition record's `above`/`below`. `Standard14Metrics` has no
bearing here. Vertical text uses the same glyph with `0x0E` commands *included* rather than skipped,
which is a different mechanism from `vmtx`/GSUB and should not be forced into it.

## Proposed API

```csharp
public sealed class ShxFont
{
    public static ShxFont LoadFromFile(string path);
    public static ShxFont Load(ReadOnlySpan<byte> data);   // throws on `shapes`

    public ShxFormat Format { get; }        // Unifont | BigFont
    public string Name { get; }             // from the font-definition record
    public int Above { get; }               // ascent, font units
    public int Below { get; }               // descent
    public bool HasVerticalForms { get; }   // modes != 0

    /// <summary>Lead-byte ranges; empty for unifont. Identifies the double-byte encoding family
    /// but not the codepage — the caller maps code to Unicode.</summary>
    public ImmutableArray<(int Start, int End)> LeadByteRanges { get; }

    public bool TryGetGlyph(int code, IGlyphSink sink, ShxTextOrientation orientation);
    public ImmutableArray<int> Codes { get; }
}
```

As built, plus: `Header` and `Modes` (the raw values behind `Format`/`HasVerticalForms`),
`UnitsPerEm` (= `Above + Below`), `HasGlyph`, `IsLeadByte`, `TryGetAdvance`, and the two stroking
entry points `TryGetStrokedOutline` / `RenderGlyph`. `IsStroked` is carried as per-face state
rather than a constant so the common interface below has somewhere to declare it.

The stroke parameters `LineCap` / `LineJoin` had to be promoted from `internal` to `public` for
this: the width, cap and join are the caller's, so they cannot be defaulted away. `OutlineStroker`
itself stays internal.

`ShxFont` stays separate from `OpenTypeFont` rather than becoming another container inside it: it
shares no tables, no `cmap`, no SFNT structure, and it is stroked rather than filled. A common
interface over "things that can emit a glyph into an `IGlyphSink`" would be the useful abstraction
if one is wanted later.

## Test fixtures

**Autodesk's stock faces cannot be bundled.** `txt.shx`, `romans.shx`, `gbcbig.shx` and the rest are
Autodesk intellectual property, and this repository is MIT end to end with every existing fixture
carrying an explicit licence note.

Two fixtures are therefore **authored from scratch** — same formats, our bytes, no third-party
content. Being synthetic they are also stronger fixtures than a real font: every opcode is present
deliberately and the expected geometry is known exactly.

| fixture | bytes | contents |
|---|---|---|
| `SharpAstroTest-unifont.shx` | 186 | 7 glyphs: `I L A O Z T -` |
| `SharpAstroTest-bigfont.shx` | 130 | 3 glyphs at `0x8141`–`0x8143`, one lead range `0x81-0x81` |

Opcode coverage, chosen to catch the mistakes that are easy to make:

| glyph | exercises |
|---|---|
| `I` | a single vertical stroke — zero **width**, which breaks per-axis normalisation |
| `-` | a single horizontal stroke — zero **height**, the mirror case |
| `L` | pen lift plus push/pop (`0x05`/`0x06`) to return to the origin |
| `A` | signed XY displacements (`0x08`) rather than packed vectors |
| `O` | **a full circle from four octant arcs (`0x0A`)** — an arc-skipping decoder returns an empty glyph, which is precisely the regression this guards |
| `Z` | a displacement run (`0x09`), a vertical-mode command (`0x0E`) that must be skipped, and a **non-empty glyph name** so the name is proven consumed rather than interpreted |
| `T` | a **subshape reference** (`0x07`) to `0x0049`. Read little-endian that is `0x4900`, which the font does not define, so only the crossbar survives — the geometry tells the two readings apart |
| `0x8143` | the bigfont **extended composition form**, placing `0x8142` into a non-square box, which a plain 1-byte reading of `0x07` desynchronises |

The generator is committed alongside them so the fixtures are reproducible and extendable rather
than opaque blobs — which earned itself: the first bigfont fixture was generated from this
document's original (wrong) layout and had to be regenerated once the corpus corrected it. Treat
the generator, not the `.shx` blobs, as the source of truth.

For validation against real faces, follow the `PDFLIB_TEST_PDF_DIR` pattern used elsewhere: an
opt-in environment variable pointing at a local directory of stock `.shx` files, with those tests
skipped when it is unset. That keeps licensed content out of the repository while still allowing a
full-coverage run locally. Implemented as `SHX_TEST_FONT_DIR` in `ShxRealFaceTests`.

**The synthetic fixtures cannot replace the corpus, and the corpus cannot replace them.** The
fixtures pin down exact geometry; only real faces surface truncated records, damaged index offsets,
files that are not SHX despite the extension, and the operand-length statistics above. Everything
this document got wrong was wrong in a way no synthetic fixture would ever have revealed, because
the fixture was generated from the same mistaken understanding as the parser.

## Phasing

| # | step | validated by | |
|---|---|---|---|
| 1 | Header sniff + record table for `unifont`; reject `shapes` | fixture loads, 7 glyphs, correct name/above/below | done |
| 2 | Opcode interpreter → `IGlyphSink`, excluding arcs | `I L A Z T -` match expected geometry exactly | done |
| 3 | Octant + fractional arcs | `O` closes to a circle; no empty round glyphs | done |
| 4 | `bigfont` index table, ranges, double-byte codes | bigfont fixture loads, all three glyphs render | done |
| 5 | Advance width + vertical forms (`0x0E` included) | metrics against the fixture's known values | done |
| 6 | Stroker integration, `IsStroked` on the face | stroked outline closes; rendered raster | done |
| 7 | Optional: `shapes` for symbol lookup by number | only if a caller needs it | not done |

Steps 1–4 are the whole of what a text-extraction or shape-matching consumer needs. 6 is what a
renderer needs.

Step 7 stayed optional but is worth re-weighing: `shapes` files turned out to be the **majority** of
`.shx` in the wild — 3,669 of 4,428 surveyed, against 170 unifont and 362 bigfont — so a caller
hitting one is the common case, not the exotic one. It needs a different lookup API
(`TryGetShape(int number)`), not a different parser; the opcode interpreter is already shared.

Measured on completion, across 4,428 files: 470,156 glyphs decode with no exception, 99.0% of them
producing geometry, 23.8M path commands, all finite, bounds within a sane multiple of the em.

## Gotchas, all learned the hard way

1. **The NUL-terminated glyph name** precedes the opcodes. Miss it and every glyph is empty while
   the font still loads cleanly.
2. **Arcs are not optional.** Skipping them loses exactly the round glyphs, and `txt.shx` will not
   reveal it because it contains none.
3. **`shapes` is not a text font.** Reject by header; do not read it with the unifont layout.
4. **Directions are not unit vectors** — the minor axis is 0.5.
5. **Bounds-check every operand.** Real faces contain truncated records; an unguarded
   `data[i + 1]` throws on `IndexOutOfRange` partway through an otherwise valid font.
6. **`0x0E` is a skip, not a no-op** — it suppresses the *following* command in horizontal text.
7. **The font name is not the SHX file name**, and the PDF records neither. Font identity can only
   be recovered by matching geometry.
8. **bigfont is a different container, not just a different header.** Records are reached through an
   index table of file offsets; unifont stores them inline. Sharing one record-reading path between
   the two overruns EOF on 95% of bigfont faces.
9. **The header is not a fixed 25 bytes.** Scan for the `0x1A`.
10. **The pen starts down.** Not announced anywhere; costs you the first stroke of 4,869 records.
11. **`0x07`'s operand is big-endian in unifont** — the only field in the format that is — and in
    bigfont a leading `0x00` means seven operand bytes, not one.
12. **`0` means eight octants for `0x0A` and zero octants for `0x0B`.** Same nibble, opposite
    meaning, and using one rule for both turns every small fractional arc into a full circle.
13. **Not every `.shx` is an SHX font.** The surveyed pack held renamed TrueType files, DWGs,
    MicroStation resources and slide libraries. 225 of 4,428. They must be refused, not parsed.
14. **A `0`-glyph font can be legitimate** (`DUMMY.SHX` is a placeholder whose index entries are all
    zeroed), so "no codes" is not by itself a parse failure — but a *systematic* misread empties
    fonts wholesale, so assert on the count across a corpus rather than on the individual file.
