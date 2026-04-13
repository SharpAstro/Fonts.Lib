// Tests copied from DIR.Lib.Tests targeting the FreeType-backed
// FreeTypeGlyphRasterizer. They are kept here as the parity goalposts:
// each one un-skips when the corresponding feature lands in
// SharpAstro.Fonts (see ROADMAP.md).
//
// The original source files are:
//   DIR.Lib/src/DIR.Lib.Tests/CmapDumpTests.cs
//   DIR.Lib/src/DIR.Lib.Tests/CmapLookupOrderTests.cs
//   DIR.Lib/src/DIR.Lib.Tests/FontInspectionTests.cs
//   DIR.Lib/src/DIR.Lib.Tests/SubsetFontGlyphTests.cs
//
// They will be ported here properly (renamed, retargeted at the
// SharpAstro.Fonts API) as features land. Until then this placeholder
// keeps the parity intent visible in the test report.

namespace SharpAstro.Fonts.Tests.Ported;

public class PortedFromDirLib
{
    [Fact(Skip = "Pending Phase 3 (rasterizer): port DIR.Lib.Tests/CmapDumpTests.cs")]
    public void CmapDumpTests_AllHints() { }

    [Fact(Skip = "Pending Phase 3 (rasterizer): port DIR.Lib.Tests/CmapLookupOrderTests.cs")]
    public void CmapLookupOrder_EmbeddedSubset_FindsViaSymbolPUA() { }

    [Fact(Skip = "Pending Phase 3 (rasterizer): port DIR.Lib.Tests/FontInspectionTests.cs")]
    public void FontInspection_DumpFontCmap_And_Glyphs() { }

    [Fact(Skip = "Pending Phase 3 (rasterizer): port DIR.Lib.Tests/SubsetFontGlyphTests.cs")]
    public void SubsetFont_CharCodeAsGID_ProducesNonEmptyGlyph() { }

    [Fact(Skip = "Pending Phase 5 (COLR v1): port DIR.Lib.Tests/RenderAcceptanceTests.cs color tests")]
    public void RenderAcceptance_ColorEmoji() { }
}
