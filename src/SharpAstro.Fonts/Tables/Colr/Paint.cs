using System.Collections.Frozen;
using System.Numerics;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Colr;

/// <summary>
/// COLR v1 paint format codes (uint8 at the start of every paint record).
/// We support all non-Var* formats; Var* variants land in Phase 7 alongside
/// the variable-font runtime.
/// </summary>
public enum PaintFormat : byte
{
    None = 0,
    ColrLayers = 1,
    Solid = 2,
    VarSolid = 3,
    LinearGradient = 4,
    VarLinearGradient = 5,
    RadialGradient = 6,
    VarRadialGradient = 7,
    SweepGradient = 8,
    VarSweepGradient = 9,
    Glyph = 10,
    ColrGlyph = 11,
    Transform = 12,
    VarTransform = 13,
    Translate = 14,
    VarTranslate = 15,
    Scale = 16,
    VarScale = 17,
    ScaleAroundCenter = 18,
    VarScaleAroundCenter = 19,
    ScaleUniform = 20,
    VarScaleUniform = 21,
    ScaleUniformAroundCenter = 22,
    VarScaleUniformAroundCenter = 23,
    Rotate = 24,
    VarRotate = 25,
    RotateAroundCenter = 26,
    VarRotateAroundCenter = 27,
    Skew = 28,
    VarSkew = 29,
    SkewAroundCenter = 30,
    VarSkewAroundCenter = 31,
    Composite = 32,
}

/// <summary>
/// Paint composite mode codes. Values match COLR spec §6.4.
/// </summary>
public enum CompositeMode : byte
{
    Clear = 0, Src = 1, Dest = 2, SrcOver = 3, DestOver = 4,
    SrcIn = 5, DestIn = 6, SrcOut = 7, DestOut = 8,
    SrcAtop = 9, DestAtop = 10, Xor = 11,
    Plus = 12, Screen = 13, Overlay = 14, Darken = 15,
    Lighten = 16, ColorDodge = 17, ColorBurn = 18, HardLight = 19,
    SoftLight = 20, Difference = 21, Exclusion = 22, Multiply = 23,
    HslHue = 24, HslSaturation = 25, HslColor = 26, HslLuminosity = 27,
}

/// <summary>
/// Color-line gradient extend mode (COLR spec §6.2.1).
/// </summary>
public enum GradientExtend : byte
{
    Pad = 0, Repeat = 1, Reflect = 2,
}

/// <summary>One stop on a gradient color line.</summary>
public readonly record struct ColorStop(float StopOffset, ushort PaletteIndex, float Alpha);

/// <summary>
/// Lazy reference to a COLR v1 paint record. Decoding happens on demand from
/// a <see cref="ReadOnlyMemory{Byte}"/> view of the original COLR table.
///
/// <para>This is a <see cref="readonly"/> struct (8 bytes pointer + 4 bytes
/// offset) so paint trees can be walked recursively with zero allocation.</para>
/// </summary>
public readonly struct PaintRef
{
    /// <summary>The slice of the COLR table that paint offsets are relative to (BaseGlyphList start).</summary>
    public ReadOnlyMemory<byte> Source { get; }
    /// <summary>Offset within <see cref="Source"/> where this paint record begins.</summary>
    public int Offset { get; }

    public PaintRef(ReadOnlyMemory<byte> source, int offset)
    {
        Source = source;
        Offset = offset;
    }

    /// <summary>True if this is the "no paint" sentinel (offset 0).</summary>
    public bool IsNull => Offset == 0;

    public PaintFormat Format => IsNull ? PaintFormat.None : (PaintFormat)Source.Span[Offset];

    private BigEndianReader Reader(int extraOffset) => new(Source.Span, Offset + extraOffset);

    /// <summary>Read a paint sub-record offset (uint24, relative to <see cref="Source"/>).</summary>
    private PaintRef ReadSubPaint(int offsetWithinRecord)
    {
        var span = Source.Span;
        var u24 = ((uint)span[Offset + offsetWithinRecord] << 16)
                | ((uint)span[Offset + offsetWithinRecord + 1] << 8)
                |  span[Offset + offsetWithinRecord + 2];
        return new PaintRef(Source, Offset + (int)u24);
    }

    // ---- Field accessors ---------------------------------------------------
    // Each accessor pair below corresponds to a paint format. The decoded
    // data is light enough that we re-read on demand rather than materializing.

    public PaintColrLayersData AsColrLayers()
    {
        var r = Reader(1);
        var num = r.ReadByte();
        var first = r.ReadUInt32();
        return new PaintColrLayersData(num, first);
    }

    public PaintSolidData AsSolid()
    {
        var r = Reader(1);
        var paletteIdx = r.ReadUInt16();
        var alpha = r.ReadF2Dot14();
        return new PaintSolidData(paletteIdx, alpha);
    }

    public PaintGlyphData AsGlyph()
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        var glyphID = r.ReadUInt16();
        return new PaintGlyphData(subPaint, glyphID);
    }

    public PaintColrGlyphData AsColrGlyph()
    {
        var r = Reader(1);
        var glyphID = r.ReadUInt16();
        return new PaintColrGlyphData(glyphID);
    }

    public PaintTransformData AsTransform()
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        // Affine2x3: { xx yx xy yy dx dy } each Fixed16.16 — but actually the spec
        // stores these at an offset-pointer to an Affine2x3 record. Format 12:
        //   format(uint8) paint(uint24) transform(Offset24 to Affine2x3 record).
        var affineOff = ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
        var ar = new BigEndianReader(Source.Span, Offset + (int)affineOff);
        var xx = ar.ReadFixed1616();
        var yx = ar.ReadFixed1616();
        var xy = ar.ReadFixed1616();
        var yy = ar.ReadFixed1616();
        var dx = ar.ReadFixed1616();
        var dy = ar.ReadFixed1616();
        return new PaintTransformData(subPaint, new Matrix3x2(xx, yx, xy, yy, dx, dy));
    }

    public PaintTranslateData AsTranslate()
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        var dx = r.ReadInt16();
        var dy = r.ReadInt16();
        return new PaintTranslateData(subPaint, dx, dy);
    }

    public PaintScaleData AsScale(bool aroundCenter, bool uniform)
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        float sx, sy, cx = 0, cy = 0;
        if (uniform)
        {
            sx = sy = r.ReadF2Dot14();
        }
        else
        {
            sx = r.ReadF2Dot14();
            sy = r.ReadF2Dot14();
        }
        if (aroundCenter)
        {
            cx = r.ReadInt16();
            cy = r.ReadInt16();
        }
        return new PaintScaleData(subPaint, sx, sy, cx, cy);
    }

    public PaintRotateData AsRotate(bool aroundCenter)
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        var angleTurns = r.ReadF2Dot14();   // F2DOT14, in units of 1.0 = 180°
        float cx = 0, cy = 0;
        if (aroundCenter)
        {
            cx = r.ReadInt16();
            cy = r.ReadInt16();
        }
        return new PaintRotateData(subPaint, angleTurns, cx, cy);
    }

    public PaintSkewData AsSkew(bool aroundCenter)
    {
        var subPaint = ReadSubPaint(1);
        var r = Reader(4);
        var xAngle = r.ReadF2Dot14();
        var yAngle = r.ReadF2Dot14();
        float cx = 0, cy = 0;
        if (aroundCenter)
        {
            cx = r.ReadInt16();
            cy = r.ReadInt16();
        }
        return new PaintSkewData(subPaint, xAngle, yAngle, cx, cy);
    }

    public PaintCompositeData AsComposite()
    {
        var src = ReadSubPaint(1);
        var r = Reader(4);
        var mode = (CompositeMode)r.ReadByte();
        var backOff = ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
        var back = new PaintRef(Source, Offset + (int)backOff);
        return new PaintCompositeData(src, back, mode);
    }

    public PaintLinearGradientData AsLinearGradient(FrozenDictionary<int, ColorStop[]> colorLineCache)
    {
        var clOff = ((uint)Source.Span[Offset + 1] << 16)
                  | ((uint)Source.Span[Offset + 2] << 8)
                  |  Source.Span[Offset + 3];
        var cl = ReadColorLine(colorLineCache, (int)clOff);
        var r = Reader(4);
        var x0 = r.ReadInt16(); var y0 = r.ReadInt16();
        var x1 = r.ReadInt16(); var y1 = r.ReadInt16();
        var x2 = r.ReadInt16(); var y2 = r.ReadInt16();
        return new PaintLinearGradientData(cl.Extend, cl.Stops,
            x0, y0, x1, y1, x2, y2);
    }

    public PaintRadialGradientData AsRadialGradient(FrozenDictionary<int, ColorStop[]> colorLineCache)
    {
        var clOff = ((uint)Source.Span[Offset + 1] << 16)
                  | ((uint)Source.Span[Offset + 2] << 8)
                  |  Source.Span[Offset + 3];
        var cl = ReadColorLine(colorLineCache, (int)clOff);
        var r = Reader(4);
        var x0 = r.ReadInt16(); var y0 = r.ReadInt16(); var r0 = r.ReadUInt16();
        var x1 = r.ReadInt16(); var y1 = r.ReadInt16(); var r1 = r.ReadUInt16();
        return new PaintRadialGradientData(cl.Extend, cl.Stops,
            x0, y0, r0, x1, y1, r1);
    }

    public PaintSweepGradientData AsSweepGradient(FrozenDictionary<int, ColorStop[]> colorLineCache)
    {
        var clOff = ((uint)Source.Span[Offset + 1] << 16)
                  | ((uint)Source.Span[Offset + 2] << 8)
                  |  Source.Span[Offset + 3];
        var cl = ReadColorLine(colorLineCache, (int)clOff);
        var r = Reader(4);
        var cx = r.ReadInt16(); var cy = r.ReadInt16();
        var startAngle = r.ReadF2Dot14();
        var endAngle   = r.ReadF2Dot14();
        return new PaintSweepGradientData(cl.Extend, cl.Stops, cx, cy, startAngle, endAngle);
    }

    private (GradientExtend Extend, ColorStop[] Stops) ReadColorLine(
        FrozenDictionary<int, ColorStop[]> _, int colorLineRelOffset)
    {
        // ColorLine: extend(uint8) numStops(uint16) ColorStop[numStops].
        // ColorStop: stopOffset(F2DOT14) paletteIndex(uint16) alpha(F2DOT14).
        var clOffset = Offset + colorLineRelOffset;
        var r = new BigEndianReader(Source.Span, clOffset);
        var extend = (GradientExtend)r.ReadByte();
        var n = r.ReadUInt16();
        var stops = new ColorStop[n];
        for (var i = 0; i < n; i++)
        {
            var stopOff = r.ReadF2Dot14();
            var palIdx = r.ReadUInt16();
            var alpha = r.ReadF2Dot14();
            stops[i] = new ColorStop(stopOff, palIdx, alpha);
        }
        return (extend, stops);
    }
}

// ---- Decoded paint record value types ---------------------------------------
//
// These are simple data records returned by PaintRef.As*. They are short-lived
// (one decode per paint walk step) and immutable.

public readonly record struct PaintColrLayersData(byte NumLayers, uint FirstLayerIndex);
public readonly record struct PaintSolidData(ushort PaletteIndex, float Alpha);
public readonly record struct PaintGlyphData(PaintRef Paint, ushort GlyphID);
public readonly record struct PaintColrGlyphData(ushort GlyphID);
public readonly record struct PaintTransformData(PaintRef Paint, Matrix3x2 Transform);
public readonly record struct PaintTranslateData(PaintRef Paint, float Dx, float Dy);
public readonly record struct PaintScaleData(PaintRef Paint, float Sx, float Sy, float Cx, float Cy);
public readonly record struct PaintRotateData(PaintRef Paint, float AngleTurns, float Cx, float Cy);
public readonly record struct PaintSkewData(PaintRef Paint, float XAngleTurns, float YAngleTurns, float Cx, float Cy);
public readonly record struct PaintCompositeData(PaintRef Source, PaintRef Backdrop, CompositeMode Mode);

public readonly record struct PaintLinearGradientData(
    GradientExtend Extend, ColorStop[] Stops,
    short X0, short Y0, short X1, short Y1, short X2, short Y2);

public readonly record struct PaintRadialGradientData(
    GradientExtend Extend, ColorStop[] Stops,
    short X0, short Y0, ushort R0, short X1, short Y1, ushort R1);

public readonly record struct PaintSweepGradientData(
    GradientExtend Extend, ColorStop[] Stops,
    short Cx, short Cy, float StartAngleTurns, float EndAngleTurns);
