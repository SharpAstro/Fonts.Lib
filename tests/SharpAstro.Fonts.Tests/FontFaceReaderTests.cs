namespace SharpAstro.Fonts.Tests;

/// <summary>
/// <see cref="FontFaceReader"/> — reads face identity by seeking to the 'name'/'OS/2' tables
/// instead of loading the font. Everything it reports must agree with a full load; the point of
/// the class is only that it gets there without touching the rest of the file.
/// </summary>
public sealed class FontFaceReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); }
            catch (IOException) { /* a leaked temp file is not worth failing a test over */ }
        }
    }

    private string WriteTemp(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpastro-facereader-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    [Theory]
    [InlineData(Fixtures.DejaVuSans, "DejaVu Sans")]
    [InlineData(Fixtures.SourceSans3, "Source Sans 3")]
    [InlineData(Fixtures.NotoSansJP, "Noto Sans JP")]
    public void ReadFaces_SingleFaceFile_ReportsOneFace(string fixture, string family)
    {
        var faces = FontFaceReader.ReadFaces(Fixtures.Path(fixture));
        faces.Length.ShouldBe(1);
        faces[0].FaceIndex.ShouldBe(0);
        faces[0].Family.ShouldBe(family);
    }

    /// <summary>The seeking reader and the full loader must not disagree about identity.</summary>
    [Theory]
    [InlineData(Fixtures.DejaVuSans)]
    [InlineData(Fixtures.SourceSans3)]
    [InlineData(Fixtures.RobotoFlex)]
    [InlineData(Fixtures.NotoColorEmoji)]
    [InlineData(Fixtures.Tahoma_Subset)]
    public void ReadFaces_AgreesWithFullLoad(string fixture)
    {
        var path = Fixtures.Path(fixture);
        var light = FontFaceReader.ReadFaces(path).ShouldHaveSingleItem();
        var full = OpenTypeFont.LoadFromFile(path);

        light.Family.ShouldBe(full.Name?.Family);
        light.Subfamily.ShouldBe(full.Name?.Subfamily);
        light.LegacyFamily.ShouldBe(full.Name?.LegacyFamily);
        light.PostScriptName.ShouldBe(full.Name?.PostScriptName);
        light.WeightClass.ShouldBe(full.Os2?.WeightClass ?? 0);
        light.IsBold.ShouldBe(full.Os2?.IsBold ?? false);
    }

    /// <summary>
    /// The case that motivates the class: every face of a collection is separately addressable.
    /// A file-name-based index can only ever see one face of a .ttc, which is how a font like
    /// Cambria Math — or any CJK face inside NotoSansCJK.ttc — becomes unreachable.
    /// </summary>
    [Fact]
    public void ReadFaces_Collection_ReportsEveryFaceWithItsIndex()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var source = File.ReadAllBytes(Fixtures.Path(Fixtures.SourceSans3));
        var path = WriteTemp(SyntheticTtc.Build(1, [dejavu, source, dejavu]), ".ttc");

        var faces = FontFaceReader.ReadFaces(path);

        faces.Length.ShouldBe(3);
        faces.Select(f => f.FaceIndex).ShouldBe([0, 1, 2]);
        faces[0].Family.ShouldBe("DejaVu Sans");
        faces[1].Family.ShouldBe("Source Sans 3");
        faces[2].Family.ShouldBe("DejaVu Sans");
        faces.ShouldAllBe(f => f.Path == path);
    }

    /// <summary>Each reported face index must load the face it claimed.</summary>
    [Fact]
    public void ReadFaces_CollectionIndices_RoundTripThroughLoad()
    {
        var dejavu = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans));
        var source = File.ReadAllBytes(Fixtures.Path(Fixtures.SourceSans3));
        var path = WriteTemp(SyntheticTtc.Build(1, [dejavu, source]), ".ttc");

        foreach (var face in FontFaceReader.ReadFaces(path))
        {
            var loaded = OpenTypeFont.LoadFromFile(path, face.FaceIndex);
            loaded.Name!.Family.ShouldBe(face.Family);
        }
    }

    /// <summary>
    /// An index scan walks whole directories, so it meets files that aren't fonts, files it
    /// can't open, and fonts that are truncated. All of those are an empty result, never a throw.
    /// </summary>
    [Fact]
    public void ReadFaces_NonFontInput_ReturnsEmpty()
    {
        FontFaceReader.ReadFaces(Path.Combine(Path.GetTempPath(), "definitely-not-here.ttf")).ShouldBeEmpty();
        FontFaceReader.ReadFaces(WriteTemp("this is not a font"u8.ToArray(), ".ttf")).ShouldBeEmpty();
        FontFaceReader.ReadFaces(WriteTemp([], ".ttf")).ShouldBeEmpty();

        // Truncated mid-directory: a valid header promising tables that aren't there.
        var truncated = File.ReadAllBytes(Fixtures.Path(Fixtures.DejaVuSans))[..64];
        FontFaceReader.ReadFaces(WriteTemp(truncated, ".ttf")).ShouldBeEmpty();
    }

    /// <summary>
    /// A subset font carries only a PostScript name. It still yields a face — the index can key
    /// it by that — rather than being dropped for want of a family.
    /// </summary>
    [Fact]
    public void ReadFaces_SubsetWithoutFamily_StillReportsPostScriptName()
    {
        var face = FontFaceReader.ReadFaces(Fixtures.Path(Fixtures.Tahoma_Subset)).ShouldHaveSingleItem();
        face.Family.ShouldBeNull();
        face.PostScriptName.ShouldBe("Tahoma");
    }
}
