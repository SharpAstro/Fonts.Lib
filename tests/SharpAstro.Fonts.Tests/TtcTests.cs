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
    /// <summary>
    /// Build a synthetic TTC wrapping <paramref name="faces"/> with a TTC v1
    /// header. Each input face is a complete standalone SFNT byte sequence;
    /// when we splat it into the TTC at a non-zero offset, we need to rewrite
    /// the table record offsets in its offset table to be absolute (i.e.,
    /// add the placement offset). Real TTCs have absolute offsets — so this
    /// fixup is exactly what produces a spec-conformant collection.
    /// </summary>
    private static byte[] BuildTtc(int majorVersion, byte[][] faces)
    {
        // Header: 4 (ttcf) + 2 (major) + 2 (minor) + 4 (numFonts) + 4 * numFonts (offsets)
        // v2 adds: 4 (dsigTag) + 4 (dsigLength) + 4 (dsigOffset) — all zero here.
        var headerSize = 4 + 2 + 2 + 4 + 4 * faces.Length + (majorVersion == 2 ? 12 : 0);
        var totalSize = headerSize;
        var faceOffsets = new uint[faces.Length];
        for (var i = 0; i < faces.Length; i++)
        {
            // Each face starts at an 8-byte-aligned offset (TTC convention).
            var aligned = (totalSize + 7) & ~7;
            faceOffsets[i] = (uint)aligned;
            totalSize = aligned + faces[i].Length;
        }

        var buf = new byte[totalSize];
        var pos = 0;
        buf[pos++] = 0x74; buf[pos++] = 0x74; buf[pos++] = 0x63; buf[pos++] = 0x66;
        buf[pos++] = 0; buf[pos++] = (byte)majorVersion;
        buf[pos++] = 0; buf[pos++] = 0;
        var num = (uint)faces.Length;
        buf[pos++] = (byte)(num >> 24); buf[pos++] = (byte)(num >> 16);
        buf[pos++] = (byte)(num >> 8);  buf[pos++] = (byte)num;
        foreach (var off in faceOffsets)
        {
            buf[pos++] = (byte)(off >> 24); buf[pos++] = (byte)(off >> 16);
            buf[pos++] = (byte)(off >> 8);  buf[pos++] = (byte)off;
        }
        if (majorVersion == 2) pos += 12;

        // Splat each face at its declared offset, then rewrite its table
        // record offsets to be absolute within the TTC.
        for (var i = 0; i < faces.Length; i++)
        {
            var dst = (int)faceOffsets[i];
            Buffer.BlockCopy(faces[i], 0, buf, dst, faces[i].Length);
            FixupOffsetTable(buf.AsSpan(dst), addToOffsets: dst);
        }
        return buf;
    }

    /// <summary>
    /// Add <paramref name="addToOffsets"/> to every table record offset in
    /// the SFNT-style offset table at the start of <paramref name="face"/>.
    /// The offset table layout is:
    ///   uint32 sfntVersion, uint16 numTables, 6 bytes searchRange/etc.,
    ///   numTables × { Tag(4), checksum(4), offset(4), length(4) }.
    /// We patch only the offset field of each record; everything else stays.
    /// </summary>
    private static void FixupOffsetTable(Span<byte> face, int addToOffsets)
    {
        // Skip uint32 sfntVersion.
        var numTables = (face[4] << 8) | face[5];
        // Records start after sfntVersion(4) + numTables(2) + searchRange(2)
        //                  + entrySelector(2) + rangeShift(2) = 12 bytes.
        var pos = 12;
        for (var t = 0; t < numTables; t++)
        {
            // record = tag(4) + checksum(4) + offset(4) + length(4)
            var off = (uint)((face[pos + 8] << 24) | (face[pos + 9] << 16) |
                             (face[pos + 10] << 8) | face[pos + 11]);
            var newOff = off + (uint)addToOffsets;
            face[pos + 8]  = (byte)(newOff >> 24);
            face[pos + 9]  = (byte)(newOff >> 16);
            face[pos + 10] = (byte)(newOff >> 8);
            face[pos + 11] = (byte)newOff;
            pos += 16;
        }
    }

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
