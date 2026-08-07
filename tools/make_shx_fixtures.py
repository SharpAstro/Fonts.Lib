"""Author minimal SHX test fixtures — unifont and bigfont — from scratch.

Autodesk's stock faces (txt.shx, romans.shx, gbcbig.shx) are their intellectual property and
cannot be bundled in an MIT-licensed repository. These are written byte by byte instead: same
formats, our bytes, no third-party content. Being synthetic they are also better fixtures — every
opcode is present deliberately and the expected geometry is known exactly, which a real font
cannot promise.

Coverage is chosen to exercise the parts a decoder gets wrong:
  * packed direction vectors (the common case)
  * pen up / pen down                    (0x01 / 0x02)
  * position push / pop                  (0x05 / 0x06)
  * signed XY displacement               (0x08)
  * a run of displacements               (0x09)
  * octant arc                           (0x0A)  <- skipping this loses every round glyph
  * vertical-mode skip                   (0x0E)
  * subshape reference                   (0x07)
  * a glyph whose name is non-empty      (the leading name is easy to misread as end-of-shape)
"""
import struct
import sys

UNI_HEADER = b"AutoCAD-86 unifont 1.0\r\n\x1a"
BIG_HEADER = b"AutoCAD-86 bigfont 1.0\r\n\x1a"


def vec(length, direction):
    """Packed vector: high nibble length 1-15, low nibble direction 0-15."""
    assert 1 <= length <= 15 and 0 <= direction <= 15
    return bytes([(length << 4) | direction])


PEN_DOWN = b"\x01"
PEN_UP = b"\x02"
PUSH = b"\x05"
POP = b"\x06"
END = b"\x00"


def xy(dx, dy):
    return b"\x08" + bytes([dx & 0xFF, dy & 0xFF])


def xy_run(*pairs):
    out = b"\x09"
    for dx, dy in pairs:
        out += bytes([dx & 0xFF, dy & 0xFF])
    return out + b"\x00\x00"


def octant(radius, start_octant, count, clockwise=False):
    sc = (start_octant << 4) | (count & 0x7)
    if clockwise:
        sc |= 0x80
    return b"\x0a" + bytes([radius, sc])


def subshape(code):
    """unifont subshape reference: a 2-byte code, HIGH BYTE FIRST.

    Big-endian here even though every length and count in the container is little-endian.
    Measured across 170 stock unifont faces: of 3,185 references, 3,181 resolve to a code
    the font actually defines when read high byte first, against 9 the other way.
    """
    return b"\x07" + bytes([(code >> 8) & 0xFF, code & 0xFF])


def subshape_composed(code, base_x, base_y, width, height):
    """bigfont extended subshape: 0x07, 0x00 escape, 2-byte code, then a placement box.

    The plain form is a single byte; a 0x00 in that position introduces this 7-operand
    composition form, which is how a CJK glyph is built out of radicals. Honouring the
    escape takes the corpus from 94.8% of records landing on their terminating 0x00 to
    99.98%, and from 56% of references resolving to 98.5%.
    """
    return b"\x07\x00" + bytes([(code >> 8) & 0xFF, code & 0xFF,
                                base_x & 0xFF, base_y & 0xFF, width, height])


def glyph(name, *parts):
    """A glyph record body: null-terminated name, then the opcode stream, then 0x00."""
    return name.encode("ascii") + b"\x00" + b"".join(parts) + END


# ---------------------------------------------------------------- unifont

def build_unifont():
    """A 7-glyph unifont. Metrics: above=8, below=2, modes=0 (horizontal only)."""
    glyphs = {}

    # 'I' — a bare vertical bar. Zero width; the case that breaks per-axis normalisation.
    glyphs[ord("I")] = glyph("", PEN_DOWN, vec(8, 4), PEN_UP)

    # '-' — a bare horizontal bar. Zero height, the mirror case.
    glyphs[ord("-")] = glyph("", PEN_UP, vec(2, 4), PEN_DOWN, vec(6, 0), PEN_UP, vec(2, 12))

    # 'L' — two strokes with a pen lift, using push/pop to return to the origin.
    glyphs[ord("L")] = glyph(
        "", PUSH, PEN_DOWN, vec(8, 4), PEN_UP, POP, PEN_DOWN, vec(5, 0), PEN_UP)

    # 'A' — apex and crossbar via signed XY displacements rather than packed vectors.
    glyphs[ord("A")] = glyph(
        "", PEN_DOWN, xy(3, 8), xy(3, -8), PEN_UP, xy(-5, 3), PEN_DOWN, xy(4, 0), PEN_UP, xy(2, -3))

    # 'O' — a full circle from four octant arcs. Exercises 0x0A; a decoder that skips arcs
    # produces an empty glyph here, which is exactly the bug this fixture is for.
    glyphs[ord("O")] = glyph(
        "", PEN_UP, xy(4, 4), PEN_DOWN,
        octant(4, 0, 2), octant(4, 2, 2), octant(4, 4, 2), octant(4, 6, 2), PEN_UP)

    # 'Z' — a run of displacements (0x09) plus a vertical-mode command that must be SKIPPED in
    # horizontal text, and a non-empty glyph name to prove the name is consumed not interpreted.
    glyphs[ord("Z")] = glyph(
        "zed", b"\x0e", vec(4, 12), PEN_DOWN, xy_run((6, 0), (-6, -8), (6, 0)), PEN_UP)

    # 'T' — a crossbar, then 'I' pulled in as a subshape (0x07). The reference is to 0x0049,
    # so a decoder reading the operand little-endian looks up 0x4900, finds nothing and draws
    # the crossbar alone: the geometry tells the two readings apart.
    glyphs[ord("T")] = glyph(
        "", PEN_UP, xy(-3, 8), PEN_DOWN, vec(6, 0), PEN_UP, xy(-3, -8), subshape(ord("I")))

    body = b""
    fontdef = b"SHARPASTRO TEST UNIFONT\x00" + bytes([8, 2, 0, 0, 0, 0])
    body += struct.pack("<H", len(fontdef)) + fontdef
    for code in sorted(glyphs):
        data = glyphs[code]
        body += struct.pack("<HH", code, len(data)) + data
    # Count includes the font-definition record.
    return UNI_HEADER + struct.pack("<I", len(glyphs) + 1) + body


# ---------------------------------------------------------------- bigfont

def build_bigfont():
    """A 3-glyph bigfont with one lead-byte range, 0x81-0x81.

    bigfont does NOT store records inline the way unifont does. After the lead-byte ranges
    comes an INDEX TABLE of 8-byte entries -- u16 code, u16 length, u32 file offset -- and
    then a contiguous data area those offsets point into. Verified against 362 stock bigfont
    faces: in 358 of them the index table abuts the data area byte-for-byte (the other 4 are
    damaged, with entry offsets past EOF). The code-0 entry is the font definition.

    Layout:
        u16  marker      (8 in 350 of 362 surveyed faces, 0 in the other 12; purpose
                          unconfirmed, and the entry size is a fixed 8 either way)
        u16  count       (index entries, including the code-0 font definition)
        u16  range count
        ...  lead-byte ranges, u16 start + u16 end each
        ...  index entries, u16 code + u16 length + u32 offset each
        ...  data area
    """
    records = {}
    # A cross and a box — deliberately simple, distinguishable at 16x16, double-byte codes.
    records[0x8141] = glyph(
        "", PEN_UP, vec(4, 12), PEN_DOWN, vec(8, 4), PEN_UP,
        xy(-4, -4), PEN_DOWN, vec(8, 0), PEN_UP)
    records[0x8142] = glyph(
        "", PEN_DOWN, vec(6, 0), vec(6, 4), vec(6, 8), vec(6, 12), PEN_UP)
    # 0x8143 — the box composed in through the extended subshape form, placed at (2,1) and
    # scaled into a 4-wide by 8-high box against above=8, so x halves and y is unchanged.
    # The trailing move proves the parent's pen is restored rather than left where the
    # composed radical finished.
    records[0x8143] = glyph(
        "", subshape_composed(0x8142, 2, 1, 4, 8), PEN_UP, vec(9, 0))

    fontdef = b"SHARPASTRO TEST BIGFONT\x00" + bytes([8, 2, 0, 0, 0, 0])
    entries = [(0, fontdef)] + [(code, records[code]) for code in sorted(records)]

    ranges = struct.pack("<HH", 0x81, 0x81)
    head = struct.pack("<HHH", 8, len(entries), 1) + ranges
    index_size = 8 * len(entries)
    data_offset = len(BIG_HEADER) + len(head) + index_size

    index = b""
    data = b""
    for code, blob in entries:
        index += struct.pack("<HHI", code, len(blob), data_offset + len(data))
        data += blob

    return BIG_HEADER + head + index + data


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    for name, data in (("SharpAstroTest-unifont.shx", build_unifont()),
                       ("SharpAstroTest-bigfont.shx", build_bigfont())):
        with open(f"{out}/{name}", "wb") as fh:
            fh.write(data)
        print(f"wrote {name}  ({len(data)} bytes)")
