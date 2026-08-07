using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Shx;

/// <summary>
/// AutoCAD SHX shape-program interpreter. Walks the byte program of one glyph record and
/// emits path commands to an <see cref="IGlyphSink"/> — the same shape of work as
/// <see cref="Type1CharstringInterpreter"/> and <see cref="Type2CharstringInterpreter"/>,
/// which is why it lives beside them rather than inside a container class.
///
/// <para>Unlike those two, the result is an <b>open pen path</b>, not closed contours:
/// <see cref="IGlyphSink.Close"/> is never called. See <see cref="ShxFont.IsStroked"/>.</para>
///
/// <para>Per-call instance state (pen, scale, position stack) lives on the calling stack and
/// never escapes, so this is safe to invoke concurrently from any thread.</para>
///
/// <para><b>Where the semantics come from.</b> The opcode set is documented in the AutoCAD
/// Customization Guide's shape/font descriptions, but several operand details are not, and
/// those were settled by measurement against 532 stock and third-party faces
/// (170 unifont, 362 bigfont, ~470,000 glyph records). Each such case is called out at the
/// point it matters. The blunt structural check is that walking a record's opcode stream
/// under these operand lengths must land on the terminating <c>0x00</c> exactly at the
/// record's last byte, which it does for 99.98% of records in both formats.</para>
/// </summary>
internal sealed class ShxShapeInterpreter
{
    /// <summary>
    /// The documented position stack is 4 deep. 16 is allowed because the cost is nothing
    /// and the corpus is not well-behaved: it holds 12,660 pushes against 12,770 pops, so
    /// some faces pop an empty stack. Both overflow and underflow are ignored rather than
    /// thrown.
    /// </summary>
    private const int PositionStackDepth = 16;

    /// <summary>Guards against a subshape that references itself, directly or in a cycle.</summary>
    private const int MaxSubshapeDepth = 8;

    private const double Octant = Math.PI / 4;

    // The 16 packed-vector directions. These are NOT unit vectors: the dominant axis is
    // 1.0 and the minor axis 0.5, which is what puts SHX diagonals on a lattice and keeps
    // them crisp at small sizes. Normalising them "correctly" visibly skews every diagonal.
    private static readonly float[] DirX =
        [1f, 1f, 1f, 0.5f, 0f, -0.5f, -1f, -1f, -1f, -1f, -1f, -0.5f, 0f, 0.5f, 1f, 1f];
    private static readonly float[] DirY =
        [0f, 0.5f, 1f, 1f, 1f, 1f, 1f, 0.5f, 0f, -0.5f, -1f, -1f, -1f, -1f, -1f, -0.5f];

    private readonly ShxFont _font;
    private readonly IGlyphSink? _sink;
    private readonly ShxTextOrientation _orientation;

    private float _x;
    private float _y;

    // Separate X and Y scales. 0x03/0x04 always move both together, but a bigfont
    // composed subshape is placed into a width x height box that is routinely not square.
    private float _scaleX = 1f;
    private float _scaleY = 1f;

    // SHX starts with the pen DOWN. Two independent signals in the corpus agree: 4,869
    // glyph records perform a drawing operation before issuing any pen command at all
    // (they would silently lose their first stroke under a pen-up default), and there are
    // 50,202 more pen-ups than pen-downs across 44,332 records — about one unmatched lift
    // per glyph, which is what a pen-down default plus a closing lift produces.
    private bool _penDown = true;

    private bool _contourOpen;

    private readonly (float X, float Y)[] _stack = new (float X, float Y)[PositionStackDepth];
    private int _sp;
    private int _depth;

    private ShxShapeInterpreter(ShxFont font, IGlyphSink? sink, ShxTextOrientation orientation)
    {
        _font = font;
        _sink = sink;
        _orientation = orientation;
    }

    /// <summary>
    /// Run one glyph record. Returns the advance width: the pen's X position at
    /// end-of-shape, which is the only advance the format states.
    /// </summary>
    /// <param name="sink">May be null to compute the advance without emitting geometry.</param>
    public static float Execute(ShxFont font, ReadOnlySpan<byte> record, IGlyphSink? sink,
        ShxTextOrientation orientation)
    {
        var it = new ShxShapeInterpreter(font, sink, orientation);
        it.RunRecord(record);
        return it._x;
    }

    /// <summary>
    /// Skip the record's glyph name, then run its opcodes.
    ///
    /// <para>Every record — glyph and font-definition alike — begins with a NUL-terminated
    /// name, usually empty and so a bare <c>0x00</c>. Starting the opcode walk at byte 0
    /// reads that as end-of-shape and returns an empty glyph for every character in the
    /// font, and nothing about it looks like a failure: the font loads cleanly and simply
    /// draws nothing.</para>
    /// </summary>
    private void RunRecord(ReadOnlySpan<byte> record)
    {
        var nul = record.IndexOf((byte)0);
        if (nul < 0) return;
        Run(record[(nul + 1)..]);
    }

    private void Run(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var op = data[i++];

            // Anything with a non-zero high nibble is a packed vector, not a command.
            if (op >= 0x10)
            {
                Step(DirX[op & 0x0F] * (op >> 4), DirY[op & 0x0F] * (op >> 4));
                continue;
            }

            switch (op)
            {
                case 0x00:                                  // end of shape
                    return;

                case 0x01:                                  // pen down
                    _penDown = true;
                    break;

                case 0x02:                                  // pen up
                    _penDown = false;
                    _contourOpen = false;                    // ends the subpath; never closes it
                    break;

                case 0x03:                                  // divide vector length
                    if (i >= data.Length) return;
                    var divisor = data[i++];
                    if (divisor != 0) { _scaleX /= divisor; _scaleY /= divisor; }
                    break;

                case 0x04:                                  // multiply vector length
                    if (i >= data.Length) return;
                    var multiplier = data[i++];
                    if (multiplier != 0) { _scaleX *= multiplier; _scaleY *= multiplier; }
                    break;

                case 0x05:                                  // push position
                    if (_sp < PositionStackDepth) _stack[_sp++] = (_x, _y);
                    break;

                case 0x06:                                  // pop position
                    if (_sp > 0)
                    {
                        (_x, _y) = _stack[--_sp];
                        _contourOpen = false;                // the jump breaks the subpath
                    }
                    break;

                case 0x07:                                  // subshape reference
                    Subshape(data, ref i);
                    break;

                case 0x08:                                  // signed XY displacement
                    if (i + 1 >= data.Length) return;
                    Step((sbyte)data[i], (sbyte)data[i + 1]);
                    i += 2;
                    break;

                case 0x09:                                  // run of displacements, (0,0)-terminated
                    while (true)
                    {
                        if (i + 1 >= data.Length) return;
                        var dx = (sbyte)data[i];
                        var dy = (sbyte)data[i + 1];
                        i += 2;
                        if (dx == 0 && dy == 0) break;
                        Step(dx, dy);
                    }
                    break;

                case 0x0A:                                  // octant arc
                    if (i + 1 >= data.Length) return;
                    OctantArc(data[i], data[i + 1]);
                    i += 2;
                    break;

                case 0x0B:                                  // fractional arc
                    if (i + 4 >= data.Length) return;
                    FractionalArc(data[i], data[i + 1], data[i + 2], data[i + 3], data[i + 4]);
                    i += 5;
                    break;

                case 0x0C:                                  // bulge arc
                    if (i + 2 >= data.Length) return;
                    BulgeArc((sbyte)data[i], (sbyte)data[i + 1], (sbyte)data[i + 2]);
                    i += 3;
                    break;

                case 0x0D:                                  // run of bulge arcs
                    while (true)
                    {
                        if (i + 1 >= data.Length) return;
                        var dx = (sbyte)data[i];
                        var dy = (sbyte)data[i + 1];
                        i += 2;
                        // Terminated by a (0,0) displacement, which carries no bulge byte.
                        if (dx == 0 && dy == 0) break;
                        if (i >= data.Length) return;
                        BulgeArc(dx, dy, (sbyte)data[i]);
                        i++;
                    }
                    break;

                case 0x0E:
                    // A SKIP, not a no-op: 0x0E suppresses the FOLLOWING command in
                    // horizontal text. In vertical text that command runs, which makes
                    // 0x0E itself the no-op. This is how SHX carries vertical forms —
                    // inside the same glyph, not in a separate one — and it is common:
                    // 77,397 occurrences across the corpus.
                    if (_orientation == ShxTextOrientation.Horizontal) SkipCommand(data, ref i);
                    break;

                default:
                    // 0x0F: high nibble 0 means length 0, so it draws nothing either way.
                    break;
            }
        }
    }

    /// <summary>
    /// Advance <paramref name="i"/> past exactly one command without executing it, for
    /// <c>0x0E</c> in horizontal text.
    /// </summary>
    private void SkipCommand(ReadOnlySpan<byte> data, ref int i)
    {
        if (i >= data.Length) return;
        var op = data[i];
        if (op == 0x00) return;                              // leave end-of-shape for Run()
        i++;
        if (op >= 0x10) return;                              // packed vector: no operands

        switch (op)
        {
            case 0x03:
            case 0x04:
                i += 1;
                break;
            case 0x07:
                SkipSubshapeOperands(data, ref i);
                break;
            case 0x08:
            case 0x0A:
                i += 2;
                break;
            case 0x0C:
                i += 3;
                break;
            case 0x0B:
                i += 5;
                break;
            case 0x09:
            case 0x0D:
                while (i + 1 < data.Length)
                {
                    var dx = data[i];
                    var dy = data[i + 1];
                    i += 2;
                    if (dx == 0 && dy == 0) break;
                    if (op == 0x0D) i++;
                }
                break;
            default:
                break;                                       // 0x01/0x02/0x05/0x06/0x0E take none
        }
        if (i > data.Length) i = data.Length;
    }

    private void SkipSubshapeOperands(ReadOnlySpan<byte> data, ref int i)
    {
        if (_font.Format == ShxFormat.Unifont) { i += 2; return; }
        i += i < data.Length && data[i] == 0 ? 7 : 1;
    }

    /// <summary>
    /// Move the pen by a displacement, drawing if it is down.
    /// </summary>
    private void Step(float dx, float dy)
    {
        var nx = _x + dx * _scaleX;
        var ny = _y + dy * _scaleY;
        if (_penDown)
        {
            EnsureContour();
            _sink?.LineTo(nx, ny);
        }
        _x = nx;
        _y = ny;
    }

    /// <summary>
    /// Open a subpath at the current point on the first drawing operation after a pen-down.
    /// Lazy so that a pen-down followed by a jump does not emit a stray <c>MoveTo</c>.
    /// </summary>
    private void EnsureContour()
    {
        if (_contourOpen) return;
        _sink?.MoveTo(_x, _y);
        _contourOpen = true;
    }

    /// <summary>
    /// <c>0x0A radius ±0SC</c>: high nibble of the second operand is the start octant,
    /// low nibble the octant count, bit 7 sets clockwise. Octants are 45 degrees each,
    /// numbered counterclockwise from 0 at 3 o'clock.
    ///
    /// <para>Skipping arcs is the mistake with the most misleading symptom: it loses
    /// exactly the round glyphs and nothing else, producing a per-character result that is
    /// perfectly bimodal — 100% on <c>T Y + 7 :</c> and 0% on <c>D O R P a c e g</c>. And
    /// <c>txt.shx</c> cannot catch it, because <c>txt</c> contains no arcs at all: its
    /// <c>D</c> is six straight segments. Test against <c>romans</c> or <c>isocp</c>.</para>
    /// </summary>
    private void OctantArc(byte radius, byte sc)
    {
        var count = sc & 0x07;
        if (count == 0) count = 8;                           // 0 means the full circle
        var start = (sc >> 4) & 0x07;
        var sweep = count * Octant * ((sc & 0x80) != 0 ? -1 : 1);
        ArcFromPen(radius, start * Octant, sweep);
    }

    /// <summary>
    /// <c>0x0B start_offset end_offset high_radius radius ±0SC</c>: an arc whose ends need
    /// not sit on octant boundaries. Offsets are in 1/256ths of an octant from the
    /// boundary; the radius is 16-bit, high byte first.
    ///
    /// <para>The octant count here means what it says, and 0 means <b>zero</b> octants —
    /// an arc contained entirely within one — where for <c>0x0A</c> it means eight. With
    /// both offsets zero this reduces exactly to <c>0x0A</c>, which is the constraint that
    /// pins the reading down.</para>
    /// </summary>
    private void FractionalArc(byte startOffset, byte endOffset, byte highRadius,
        byte lowRadius, byte sc)
    {
        var radius = (highRadius << 8) | lowRadius;
        var start = (sc >> 4) & 0x07;
        var count = sc & 0x07;
        var dir = (sc & 0x80) != 0 ? -1 : 1;

        var a0 = start * Octant + dir * (startOffset / 256.0) * Octant;
        var a1 = (start + dir * count) * Octant + dir * (endOffset / 256.0) * Octant;
        ArcFromPen(radius, a0, a1 - a0);
    }

    /// <summary>
    /// An arc whose start point is the pen. The current point lies <b>on</b> the circle at
    /// <paramref name="startAngle"/>, so the centre is back along that radius — the format
    /// never states a centre.
    /// </summary>
    private void ArcFromPen(float radius, double startAngle, double sweep)
        => Arc(
            (float)(-radius * Math.Cos(startAngle)),
            (float)(-radius * Math.Sin(startAngle)),
            radius, startAngle, sweep);

    /// <summary>
    /// <c>0x0C dx dy bulge</c>: an arc over the chord (dx, dy). The bulge is
    /// <c>127 * 2H/D</c> for chord length D and arc height H, so |bulge| = 127 is a
    /// semicircle and it cannot express more. Positive is counterclockwise. Zero is a
    /// straight line, which faces do use.
    /// </summary>
    private void BulgeArc(sbyte dx, sbyte dy, sbyte bulge)
    {
        if (bulge == 0) { Step(dx, dy); return; }

        float vx = dx, vy = dy;
        var chord = MathF.Sqrt(vx * vx + vy * vy);
        if (chord <= 0f) return;

        var half = chord * 0.5f;
        var height = Math.Abs(bulge) / 127f * half;
        if (height <= 0f) { Step(dx, dy); return; }
        var radius = (half * half + height * height) / (2f * height);

        // From the chord midpoint, (radius - height) along the chord's left-hand normal.
        // Counterclockwise (positive bulge) puts the centre on the left and bows the arc
        // to the right of the chord; negative mirrors both.
        var sign = bulge > 0 ? 1f : -1f;
        var nx = -vy / chord;
        var ny = vx / chord;
        var cx = vx * 0.5f + sign * (radius - height) * nx;
        var cy = vy * 0.5f + sign * (radius - height) * ny;

        // The pen is the local origin, so its angle about the centre is atan2(-cy, -cx).
        var a0 = Math.Atan2(-cy, -cx);
        var sweep = sign * 2.0 * Math.Asin(Math.Min(1.0, half / radius));
        Arc(cx, cy, radius, a0, sweep);
    }

    /// <summary>
    /// Emit an arc as cubic Béziers, one per 45 degrees or less.
    ///
    /// <para>Arcs become <see cref="IGlyphSink.CubicTo"/> rather than the polyline a quick
    /// implementation would reach for. Geometry is computed in unscaled units with the pen
    /// at the local origin and every emitted point mapped out through the X and Y scales;
    /// Béziers are affine-invariant, so scaling the control points is exact even when those
    /// scales differ, as they do inside a bigfont composition box.</para>
    /// </summary>
    /// <param name="cx">Centre X, unscaled, relative to the pen.</param>
    /// <param name="cy">Centre Y, unscaled, relative to the pen.</param>
    private void Arc(float cx, float cy, float radius, double startAngle, double sweep)
    {
        if (radius <= 0f || Math.Abs(sweep) < 1e-9) return;

        var segments = (int)Math.Ceiling(Math.Abs(sweep) / Octant);
        if (segments < 1) segments = 1;
        var step = sweep / segments;
        var k = 4.0 / 3.0 * Math.Tan(step / 4.0);

        var ox = _x;
        var oy = _y;
        if (_penDown) EnsureContour();

        for (var s = 0; s < segments; s++)
        {
            var t0 = startAngle + step * s;
            var t1 = t0 + step;
            var cos0 = Math.Cos(t0);
            var sin0 = Math.Sin(t0);
            var cos1 = Math.Cos(t1);
            var sin1 = Math.Sin(t1);

            var p0x = cx + radius * cos0;
            var p0y = cy + radius * sin0;
            var p3x = cx + radius * cos1;
            var p3y = cy + radius * sin1;

            if (_penDown)
            {
                _sink?.CubicTo(
                    MapX(ox, p0x - k * radius * sin0), MapY(oy, p0y + k * radius * cos0),
                    MapX(ox, p3x + k * radius * sin1), MapY(oy, p3y - k * radius * cos1),
                    MapX(ox, p3x), MapY(oy, p3y));
            }
        }

        _x = MapX(ox, cx + radius * Math.Cos(startAngle + sweep));
        _y = MapY(oy, cy + radius * Math.Sin(startAngle + sweep));
    }

    private float MapX(float origin, double local) => origin + (float)local * _scaleX;
    private float MapY(float origin, double local) => origin + (float)local * _scaleY;

    /// <summary>
    /// <c>0x07</c>: run another record's opcodes inline, inheriting the pen, scale and
    /// position.
    ///
    /// <para><b>unifont</b> takes a 2-byte code, <b>high byte first</b> — the opposite of
    /// every length and count field in the container, which are all little-endian. Settled
    /// by measurement: of 3,185 references across 170 stock unifont faces, 3,181 resolve to
    /// a code the font actually defines when read high byte first, against 9 the other
    /// way.</para>
    ///
    /// <para><b>bigfont</b> takes one byte, unless that byte is <c>0x00</c>, which
    /// introduces the extended composition form used to build a CJK glyph out of radicals:
    /// <c>0x00, code_hi, code_lo, base_x, base_y, width, height</c>. Reading it as a plain
    /// 1-byte operand throughout gets 94.8% of records landing on their terminator and 56%
    /// of references resolving; honouring the escape gets 99.98% and 98.5%.</para>
    /// </summary>
    private void Subshape(ReadOnlySpan<byte> data, ref int i)
    {
        if (_font.Format == ShxFormat.Unifont)
        {
            if (i + 1 >= data.Length) { i = data.Length; return; }
            var code = (data[i] << 8) | data[i + 1];
            i += 2;
            RunSubshape(code);
            return;
        }

        if (i >= data.Length) return;
        if (data[i] != 0)
        {
            RunSubshape(data[i]);
            i++;
            return;
        }

        if (i + 6 >= data.Length) { i = data.Length; return; }
        var composed = (data[i + 1] << 8) | data[i + 2];
        var baseX = (sbyte)data[i + 3];
        var baseY = (sbyte)data[i + 4];
        var width = data[i + 5];
        var height = data[i + 6];
        i += 7;
        RunComposedSubshape(composed, baseX, baseY, width, height);
    }

    private void RunSubshape(int code)
    {
        if (_depth >= MaxSubshapeDepth) return;
        if (!_font.TryGetRecord(code, out var record)) return;
        _depth++;
        RunRecord(record);
        _depth--;
    }

    /// <summary>
    /// The bigfont extended composition form: draw <paramref name="code"/> offset by
    /// (<paramref name="baseX"/>, <paramref name="baseY"/>) and scaled into a
    /// <paramref name="width"/> x <paramref name="height"/> box.
    ///
    /// <para>The box is in font units of the same magnitude as <see cref="ShxFont.Above"/>,
    /// not a fixed-point fraction: the most common observed triple is
    /// (above 60, width 59, height 60) and (above 15, height 15) dominates with the width
    /// varying, i.e. full-height radicals of differing widths. So the scale is taken as
    /// width/above by height/above. The parent pen is restored afterwards, since base_x and
    /// base_y are 0 in the plurality of cases, which reads as each radical being placed
    /// from a common origin.</para>
    ///
    /// <para>These placement semantics are inferred from those corpus statistics rather
    /// than from a specification, and the geometry is exact for the dominant
    /// height == above case.</para>
    /// </summary>
    private void RunComposedSubshape(int code, int baseX, int baseY, int width, int height)
    {
        if (_depth >= MaxSubshapeDepth) return;
        if (!_font.TryGetRecord(code, out var record)) return;

        var savedX = _x;
        var savedY = _y;
        var savedScaleX = _scaleX;
        var savedScaleY = _scaleY;
        var savedPen = _penDown;

        var reference = _font.Above > 0 ? _font.Above : 1;
        _x += baseX * _scaleX;
        _y += baseY * _scaleY;
        if (width > 0) _scaleX = savedScaleX * (width / (float)reference);
        if (height > 0) _scaleY = savedScaleY * (height / (float)reference);
        _contourOpen = false;

        _depth++;
        RunRecord(record);
        _depth--;

        _x = savedX;
        _y = savedY;
        _scaleX = savedScaleX;
        _scaleY = savedScaleY;
        _penDown = savedPen;
        _contourOpen = false;
    }
}
