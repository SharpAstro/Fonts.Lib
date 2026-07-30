using SharpAstro.Fonts.Tables.Sfnt;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// TrueType Collection (TTC) loader tests. The repo doesn't ship a real TTC
/// fixture, so we synthesize one on the fly by wrapping the bundled
/// DejaVuSans.ttf inside a TTC v1 (and separately v2) header. That exercises
/// the magic detection, header parsing, face-offset dispatch, and per-face
/// table directory parsing — all without an external file dependency.
///
/// A separate sanity test loads <c>cambria.ttc</c> when present on Windows,
/// so it's useful as a local smoke test against a real-world TTC but skips on
/// Linux/macOS CI.
/// </summary>
public class TtcTests
{
    // Shared with FontFaceReaderTests, which needs the same multi-face collection.
    private static byte[] BuildTtc(int majorVersion, byte[][] faces)
        => SyntheticTtc.Build(majorVersion, faces);

    [Fact]
    public void IsTtc_DetectsMagic()
    {
        TtcHeader.IsTtc([0x74, 0x74, 0x63, 0x66, 0, 0, 0, 0]).ShouldBeTrue();
        // Plain SFNT (TrueType): version 0x00010000.
        TtcHeader.IsTtc([0x00, 0x01, 0x00, 0x00, 0, 0, 0, 0]).ShouldBeFalse();
        // CFF SFNT: 'OTTO'.
        TtcHeader.IsTtc([0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0]).ShouldBeFalse();
        // Too short: defensive — must not throw.
        TtcHeader.IsTtc([0x74, 0x74, 0x63]).ShouldBeFalse();
    }

    [Fact]
    public void Parse_V1_ExposesFaceOffsets()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(majorVersion: 1, [dejavu, dejavu]);

        var header = TtcHeader.Parse(ttc);
        header.MajorVersion.ShouldBe((ushort)1);
        header.NumFonts.ShouldBe(2);
        header.OffsetTable.Length.ShouldBe(2);
        header.OffsetTable[0].ShouldBeLessThan(header.OffsetTable[1]);
    }

    [Fact]
    public void Parse_V2_AcceptsHeader()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(majorVersion: 2, [dejavu]);

        var header = TtcHeader.Parse(ttc);
        header.MajorVersion.ShouldBe((ushort)2);
        header.NumFonts.ShouldBe(1);
    }

    [Fact]
    public void Parse_RejectsBadMagic()
    {
        Should.Throw<InvalidDataException>(() =>
            TtcHeader.Parse([0x00, 0x01, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Parse_RejectsUnsupportedVersion()
    {
        // ttcf, version 3.0 — not in the spec.
        Should.Throw<InvalidDataException>(() =>
            TtcHeader.Parse([0x74, 0x74, 0x63, 0x66, 0, 3, 0, 0, 0, 0, 0, 1, 0, 0, 0, 16]));
    }

    [Fact]
    public void Load_TtcByDefault_PicksFaceZero()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(1, [dejavu, dejavu]);

        var font = OpenTypeFont.Load(ttc);
        font.NumGlyphs.ShouldBeGreaterThan((ushort)0);
        font.Directory.IsTrueType.ShouldBeTrue();
    }

    [Fact]
    public void Load_TtcWithFaceIndex_PicksRightFace()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(1, [dejavu, dejavu]);

        var face0 = OpenTypeFont.Load(ttc, faceIndex: 0);
        var face1 = OpenTypeFont.Load(ttc, faceIndex: 1);
        face0.NumGlyphs.ShouldBe(face1.NumGlyphs);
        face0.UnitsPerEm.ShouldBe(face1.UnitsPerEm);
    }

    [Fact]
    public void Load_TtcWithFaceIndexOutOfRange_Throws()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(1, [dejavu]);

        Should.Throw<ArgumentOutOfRangeException>(() => OpenTypeFont.Load(ttc, faceIndex: 1));
        Should.Throw<ArgumentOutOfRangeException>(() => OpenTypeFont.Load(ttc, faceIndex: -1));
    }

    [Fact]
    public void Load_PlainSfntWithFaceIndexNonZero_Throws()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        // Plain SFNT — only face 0 exists.
        Should.Throw<ArgumentOutOfRangeException>(() => OpenTypeFont.Load(dejavu, faceIndex: 1));
    }

    [Fact]
    public void LoadAll_OnPlainSfnt_ReturnsSingleFace()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var faces = OpenTypeFont.LoadAll(dejavu);
        faces.Length.ShouldBe(1);
        faces[0].NumGlyphs.ShouldBeGreaterThan((ushort)0);
    }

    [Fact]
    public void LoadAll_OnTtc_ReturnsAllFaces()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var ttc = BuildTtc(1, [dejavu, dejavu, dejavu]);
        var faces = OpenTypeFont.LoadAll(ttc);
        faces.Length.ShouldBe(3);
        foreach (var f in faces)
            f.NumGlyphs.ShouldBeGreaterThan((ushort)0);
    }

    /// <summary>
    /// Local sanity check against Windows' real <c>cambria.ttc</c>. Skips
    /// silently when not running on Windows or when the file isn't present
    /// (e.g. Windows Server SKUs without the supplemental Office fonts).
    /// </summary>
    [Fact]
    public void Load_RealWorldCambriaTtc_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string CambriaPath = @"C:\Windows\Fonts\cambria.ttc";
        if (!File.Exists(CambriaPath)) return;

        var faces = OpenTypeFont.LoadAllFromFile(CambriaPath);
        faces.Length.ShouldBeGreaterThan(0);
        // Cambria.ttc historically ships at least Cambria + Cambria Math.
        faces[0].NumGlyphs.ShouldBeGreaterThan((ushort)0);
    }
}
