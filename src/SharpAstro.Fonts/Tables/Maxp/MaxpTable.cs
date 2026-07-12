using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Maxp;

/// <summary>
/// Parsed 'maxp' table. Both v0.5 (CFF, 6 bytes) and v1.0 (TT, 32 bytes) supported.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/maxp
/// </summary>
public sealed class MaxpTable
{
    public uint Version { get; }
    public ushort NumGlyphs { get; }

    // v1.0 only — fields after numGlyphs. Sized to spec maximums when v0.5.
    public ushort MaxPoints { get; }
    public ushort MaxContours { get; }
    public ushort MaxCompositePoints { get; }
    public ushort MaxCompositeContours { get; }
    public ushort MaxZones { get; }
    public ushort MaxTwilightPoints { get; }
    public ushort MaxStorage { get; }
    public ushort MaxFunctionDefs { get; }
    public ushort MaxInstructionDefs { get; }
    public ushort MaxStackElements { get; }
    public ushort MaxSizeOfInstructions { get; }
    public ushort MaxComponentElements { get; }
    public ushort MaxComponentDepth { get; }

    private MaxpTable(uint version, ushort numGlyphs,
        ushort maxPoints, ushort maxContours, ushort maxCompositePoints, ushort maxCompositeContours,
        ushort maxZones, ushort maxTwilightPoints, ushort maxStorage, ushort maxFunctionDefs,
        ushort maxInstructionDefs, ushort maxStackElements, ushort maxSizeOfInstructions,
        ushort maxComponentElements, ushort maxComponentDepth)
    {
        Version = version;
        NumGlyphs = numGlyphs;
        MaxPoints = maxPoints;
        MaxContours = maxContours;
        MaxCompositePoints = maxCompositePoints;
        MaxCompositeContours = maxCompositeContours;
        MaxZones = maxZones;
        MaxTwilightPoints = maxTwilightPoints;
        MaxStorage = maxStorage;
        MaxFunctionDefs = maxFunctionDefs;
        MaxInstructionDefs = maxInstructionDefs;
        MaxStackElements = maxStackElements;
        MaxSizeOfInstructions = maxSizeOfInstructions;
        MaxComponentElements = maxComponentElements;
        MaxComponentDepth = maxComponentDepth;
    }

    /// <summary>
    /// Synthesize a v0.5 'maxp' for a bare CFF program, whose glyph count is the
    /// CharStrings INDEX count. CFF has no TrueType hinting, so the v1.0 fields keep
    /// their v0.5 defaults (MaxZones = 2, everything else 0) — same as
    /// <see cref="Parse"/> takes for a real v0.5 table.
    /// </summary>
    internal static MaxpTable ForCff(ushort numGlyphs)
        => new(version: 0x00005000, numGlyphs,
            0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0);

    public static MaxpTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt32();
        var numGlyphs = r.ReadUInt16();
        if (version < 0x00010000) // v0.5 — CFF, no hinting fields
        {
            return new MaxpTable(version, numGlyphs,
                0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        // v1.0 — TrueType
        var maxPoints = r.ReadUInt16();
        var maxContours = r.ReadUInt16();
        var maxCompPoints = r.ReadUInt16();
        var maxCompContours = r.ReadUInt16();
        var maxZones = r.ReadUInt16();
        var maxTwilightPoints = r.ReadUInt16();
        var maxStorage = r.ReadUInt16();
        var maxFunctionDefs = r.ReadUInt16();
        var maxInstructionDefs = r.ReadUInt16();
        var maxStackElements = r.ReadUInt16();
        var maxSizeOfInstructions = r.ReadUInt16();
        var maxComponentElements = r.ReadUInt16();
        var maxComponentDepth = r.ReadUInt16();
        return new MaxpTable(version, numGlyphs,
            maxPoints, maxContours, maxCompPoints, maxCompContours,
            maxZones, maxTwilightPoints, maxStorage, maxFunctionDefs,
            maxInstructionDefs, maxStackElements, maxSizeOfInstructions,
            maxComponentElements, maxComponentDepth);
    }
}
