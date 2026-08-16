# Changelog

Release notes for SharpAstro.Fonts and SharpAstro.Fonts.Shaping, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in the repo-root `Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.

## 1.11

Embedded PDF subsets carrying ONLY a Mac Roman (1,0) cmap now
select glyphs through it instead of guessing.
  * GlyphMapHint.EmbeddedSubset skipped the (1,0) subtable outright. The skip was added for
    Tahoma/ISOCPEUR-style subsets, where (1,0) only duplicates the (3,0) symbol cmap that
    already answers first -- but macOS Quartz writes simple-TrueType subsets whose ONLY
    subtable is (1,0), and there the skip left nothing except the direct-GID fallback:
    the char code used as a glyph index. Korean body text came out as unrelated glyphs in
    codepoint-sorted order (the subset's own glyph order, which is what made it look like
    plausible-but-wrong Hangul), while the same file's Latin and punctuation subsets -- 2
    to 20 glyphs against char codes like 44 and 52 -- indexed past the end of the glyph
    array and rendered as nothing at all, so commas, periods and page numbers vanished.
  * (1,0) now ranks last among the real subtables and ahead of the guess. Measured against
    the bundled subsets, that changes the answer for zero char codes in XXTIIT+Arial,
    Tahoma and ISOCPEUR: their (3,0) table answers first for every code it covers.
Behaviour change is confined to lookups that previously reached the direct-GID fallback --
i.e. only where the answer was already a guess.

## 1.10

Hinting and RTL mark-attachment fixes, plus one additive API.
  * THREE compounding TrueType interpreter defects, each masking the next. Zone's
    constructor never set PointCount, so the twilight zone -- which nothing else ever
    sizes, unlike the glyph zone -- reported 0 points forever and every twilight
    operation was a silent no-op. ExecAlignRp's out-of-range-rp0 guard returned WITHOUT
    popping its operands; fonts drive ALIGNRP from a LOOPCALL'd helper, so the surplus
    accumulated until the enclosing "call until the stack drains" loop could never reach
    its exit depth, which is what hung 'g' and 'x' outright. And 'cvt ' was read as
    ushort when FWORD is SIGNED int16, turning 26 of NotoSans-Regular's 150 control
    values into huge positives -- hidden entirely by the dead twilight zone, and invisible
    in DejaVuSans, which has no negative entries at all. Fixing any one alone made things
    WORSE (24 and 139 newly malformed glyphs respectively); all three give 0 of 1580
    glyph-size pairs out of proportion, down from 139 with the worst 80x too tall. Also
    fixed alongside: the FDEF/IDEF skip scanned raw bytes for ENDF (0x2D = 45, a perfectly
    ordinary push operand) instead of stepping whole instructions.
  * GPOS mark-attachment offsets propagate the RTL way in RTL runs. Only HarfBuzz's
    forward branch was implemented and it was applied in both directions, so every
    attached mark in Arabic sat exactly one base-glyph advance from where it belongs.
  * Additive API: NameId.License / NameId.LicenseUrl (name IDs 13/14) and the matching
    NameTable.License / .LicenseUrl. The parser already retained every name ID; this
    names what was already reachable, so a caller can read a face's own licensing terms.
Behaviour changes for any TrueType face whose hint programs touch the twilight zone or a
negative control value, and for RTL text with marks -- in every case from wrong to right.

## 1.8

PDF subset fonts that used to be rejected outright now load,
and glyphs can be selected by PostScript name. Two independent fixes, both driven by
real PDF-embedded subsets:
  * cmap parsing is tolerant. A subsetter that overstates a subtable's length (Canon's
    2008-era Distiller declares its (3,1) format-4 six bytes past the physical table)
    used to throw, and one bad subtable rejected the entire font -- every glyph in it
    fell back to a system face, or to nothing at all for a symbol font with no system
    equivalent. Format 4 now clamps glyphIdArray to the physical table, and each format
    parser validates its declared counts up front (TryParse, no exceptions as control
    flow) so a malformed subtable is dropped while the rest of the font stands. The
    count checks double as allocation guards for the uint32-counted formats 12 and 14.
  * OpenTypeFont.GetGlyphIdByName resolves a PostScript glyph name through the CFF
    charset (391 standard strings + the font's String INDEX, which was parsed and then
    discarded). This is the lookup a PDF simple font's /Encoding designates -- char code
    to glyph name to glyph -- and for a bare name-keyed CFF carrying no Encoding operator
    it is the only route that exists. Name-keyed fonts now parse their charset too, not
    just CID-keyed ones; a predefined charset keeps SID == GID. CID-keyed fonts and
    TrueType outlines return 0 (no name authority) so callers fall back to cmap lookups.
Purely additive; no existing behaviour changes for a well-formed font.

## 1.7

Faces can now report who they are. OpenTypeFont.Name
and .Os2 parse the 'name' / 'OS/2' tables lazily (rendering never reads
them), IsSymbolEncoded flags the legacy (3,0)-cmap fonts, and FontFaceReader
lists a file's faces — family, subfamily, PostScript name, weight, style,
and the collection index needed to reach any face past the first in a .ttc —
by seeking to those two tables instead of loading the font. Purely additive.

## 1.4

'cmap' table is now optional. PDF Identity-H subset
fonts routinely strip the cmap (CID maps directly to GID; Unicode lookup
lives in the PDF's /ToUnicode CMap instead) and previously failed to load
with "Missing required 'cmap' table". OpenTypeFont.Cmap is now nullable;
GetGlyphId handles the null case for CharCodeIsGID / EmbeddedSubset hints.
Source-compatible for callers that always go through GetGlyphId — direct
readers of the Cmap property must accommodate nullability.
The NUMBER is deliberately not here any more: it lives in the repo-root
Directory.Build.props (VersionMajorMinor) and the build job reads it back, so it
cannot drift from what the two packages declare. Add the release note above when
you bump it there. (The notes stay in this file because several contain a double
hyphen, which is illegal inside an XML comment.)
Within 1.7, no X.Y bump and NO value change: that props file now STATES
AssemblyVersion = $(VersionMajorMinor).0.0 rather than leaving it to the SDK
default. The default resolved to the same 1.7.0.0, so both packages ship
identically -- the point is that a default is fragile. The moment any csproj here
sets its own AssemblyVersion it wins silently, and CI stamps -p:Version and
-p:FileVersion but never -p:AssemblyVersion, so nothing would correct it. That is
how DIR.Lib shipped 6.4.0.0 for two majors and SdlVulkan.Renderer shipped
6.11.0.0. All seven sibling repos now state the rule once, in the props file.
