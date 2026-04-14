using System.Runtime.InteropServices;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Parses a "simple" (non-composite) glyph from the 'glyf' table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/glyf
///
/// Stateless — safe to call from any thread.
/// </summary>
internal static class SimpleGlyphParser
{
    // Simple glyph flag bits.
    private const byte OnCurve         = 0x01;
    private const byte XShortVector    = 0x02;
    private const byte YShortVector    = 0x04;
    private const byte Repeat          = 0x08;
    private const byte XIsSameOrSign   = 0x10; // dual-purpose; see spec
    private const byte YIsSameOrSign   = 0x20;
    // 0x40, 0x80 reserved / overlap-simple

    /// <summary>
    /// Parse a simple glyph whose header has already been read (bounds + numContours).
    /// <paramref name="r"/> must be positioned just past the header.
    /// </summary>
    public static Outline Parse(ref BigEndianReader r, short numContours,
        (short xMin, short yMin, short xMax, short yMax) bounds)
    {
        if (numContours == 0)
            return Outline.Empty;

        // contourEnds (uint16 × numContours)
        var contourEnds = new int[numContours];
        for (var i = 0; i < numContours; i++)
            contourEnds[i] = r.ReadUInt16();

        var pointCount = contourEnds[^1] + 1;

        // instructionLength (uint16) + instructions (uint8 × n). Captured for
        // Phase 8 hinting; consumers that don't hint can ignore Outline.Instructions.
        var instructionLength = r.ReadUInt16();
        byte[]? instructions = null;
        if (instructionLength > 0)
        {
            instructions = new byte[instructionLength];
            for (var i = 0; i < instructionLength; i++) instructions[i] = r.ReadByte();
        }

        // Flags array (length = pointCount, with run-length compression).
        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            var f = r.ReadByte();
            flags[i] = f;
            if ((f & Repeat) != 0)
            {
                var repeatCount = r.ReadByte();
                for (var j = 0; j < repeatCount; j++)
                    flags[++i] = f;
            }
        }

        var xs = new short[pointCount];
        var ys = new short[pointCount];

        // X coordinates (delta-encoded).
        short x = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var f = flags[i];
            if ((f & XShortVector) != 0)
            {
                int dx = r.ReadByte();
                if ((f & XIsSameOrSign) == 0) dx = -dx;
                x += (short)dx;
            }
            else if ((f & XIsSameOrSign) == 0)
            {
                x += r.ReadInt16();
            }
            // else: x unchanged (XIsSameOrSign + !XShortVector means "same as previous")
            xs[i] = x;
        }

        // Y coordinates.
        short y = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var f = flags[i];
            if ((f & YShortVector) != 0)
            {
                int dy = r.ReadByte();
                if ((f & YIsSameOrSign) == 0) dy = -dy;
                y += (short)dy;
            }
            else if ((f & YIsSameOrSign) == 0)
            {
                y += r.ReadInt16();
            }
            ys[i] = y;
        }

        // Mask flags down to bit 0 (on-curve) — we don't keep the parser-only bits.
        for (var i = 0; i < pointCount; i++)
            flags[i] &= OnCurve;

        return new Outline(
            ImmutableCollectionsMarshal.AsImmutableArray(xs),
            ImmutableCollectionsMarshal.AsImmutableArray(ys),
            ImmutableCollectionsMarshal.AsImmutableArray(flags),
            ImmutableCollectionsMarshal.AsImmutableArray(contourEnds),
            bounds, instructions);
    }
}
