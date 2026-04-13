using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Fvar;

/// <summary>One axis defined in 'fvar' (e.g. weight, width, optical size).</summary>
public sealed class FvarAxis
{
    public Tag Tag { get; }
    public float Min { get; }
    public float Default { get; }
    public float Max { get; }
    public ushort Flags { get; }
    public ushort NameId { get; }

    public FvarAxis(Tag tag, float min, float def, float max, ushort flags, ushort nameId)
    {
        Tag = tag;
        Min = min;
        Default = def;
        Max = max;
        Flags = flags;
        NameId = nameId;
    }

    /// <summary>
    /// Normalize a user-space value to [-1, 1] per the OpenType axis
    /// normalization rules: default → 0, min → -1, max → +1, with linear
    /// interpolation within each segment.
    /// </summary>
    public float Normalize(float userValue)
    {
        var v = Math.Clamp(userValue, Min, Max);
        if (v == Default) return 0f;
        if (v < Default)
        {
            var span = Default - Min;
            return span > 0 ? (v - Default) / span : 0f;
        }
        else
        {
            var span = Max - Default;
            return span > 0 ? (v - Default) / span : 0f;
        }
    }
}

/// <summary>One named instance from 'fvar' (e.g. "Bold", "Light Italic").</summary>
public sealed class FvarNamedInstance
{
    public ushort SubfamilyNameId { get; }
    public ushort PostScriptNameId { get; }
    public float[] Coordinates { get; }

    public FvarNamedInstance(ushort subfamilyNameId, ushort postScriptNameId, float[] coords)
    {
        SubfamilyNameId = subfamilyNameId;
        PostScriptNameId = postScriptNameId;
        Coordinates = coords;
    }
}

/// <summary>
/// Parsed 'fvar' (Font Variations) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/fvar
/// </summary>
public sealed class FvarTable
{
    public FvarAxis[] Axes { get; }
    public FvarNamedInstance[] NamedInstances { get; }

    private FvarTable(FvarAxis[] axes, FvarNamedInstance[] namedInstances)
    {
        Axes = axes;
        NamedInstances = namedInstances;
    }

    /// <summary>Default-axis-coords vector (every axis at its default value).</summary>
    public float[] DefaultCoords()
    {
        var arr = new float[Axes.Length];
        for (var i = 0; i < arr.Length; i++) arr[i] = Axes[i].Default;
        return arr;
    }

    /// <summary>Find an axis by tag (e.g. "wght"), or -1 if not present.</summary>
    public int FindAxisIndex(Tag tag)
    {
        for (var i = 0; i < Axes.Length; i++)
            if (Axes[i].Tag == tag) return i;
        return -1;
    }

    public static FvarTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // majorVersion + minorVersion
        r.Skip(4);
        var axesArrayOffset = r.ReadUInt16();
        // reserved
        r.Skip(2);
        var axisCount = r.ReadUInt16();
        var axisSize = r.ReadUInt16();
        var instanceCount = r.ReadUInt16();
        var instanceSize = r.ReadUInt16();

        var axes = new FvarAxis[axisCount];
        for (var i = 0; i < axisCount; i++)
        {
            var ar = new BigEndianReader(data, axesArrayOffset + i * axisSize);
            var tag = ar.ReadTag();
            var min = ar.ReadFixed1616();
            var def = ar.ReadFixed1616();
            var max = ar.ReadFixed1616();
            var flags = ar.ReadUInt16();
            var nameId = ar.ReadUInt16();
            axes[i] = new FvarAxis(tag, min, def, max, flags, nameId);
        }

        var instancesOffset = axesArrayOffset + axisCount * axisSize;
        var hasPostScriptNameId = instanceSize >= 4 + axisCount * 4 + 2;
        var instances = new FvarNamedInstance[instanceCount];
        for (var i = 0; i < instanceCount; i++)
        {
            var ir = new BigEndianReader(data, instancesOffset + i * instanceSize);
            var subId = ir.ReadUInt16();
            // flags
            ir.Skip(2);
            var coords = new float[axisCount];
            for (var k = 0; k < axisCount; k++) coords[k] = ir.ReadFixed1616();
            ushort psId = 0;
            if (hasPostScriptNameId) psId = ir.ReadUInt16();
            instances[i] = new FvarNamedInstance(subId, psId, coords);
        }

        return new FvarTable(axes, instances);
    }
}
