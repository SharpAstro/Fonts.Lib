using SharpAstro.Fonts.Tables.Cff;

namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Type-2 charstring interpreter. Decodes CFF glyph procedures into outline
/// path commands emitted to an <see cref="IGlyphSink"/>.
///
/// <para>Spec: Adobe Technical Note #5177 (Type 2 Charstring Format).</para>
///
/// <para>One instance per glyph render — instance state (operand stack,
/// current point, hint count) lives only on the calling stack, never
/// escapes. Safe to invoke concurrently from any thread.</para>
///
/// <para>Implemented operators: all path operators + callsubr / callgsubr /
/// return / endchar / hstem / vstem / hstemhm / vstemhm / hintmask / cntrmask.
/// Hint operators only consume their stack args + skip the mask bytes; we do
/// no bytecode hinting (Phase 8). The two-byte operators are implemented too: the four flex operators
/// (drawn as their two curves, never flattened) and the arithmetic, logic and transient-array operators.
/// They were once skipped as "vanishingly rare", which flex is not: every CFF converted from Type 1 uses it.</para>
/// </summary>
internal sealed class Type2CharstringInterpreter
{
    // Operand stack (CFF1 max 48; CFF2 increases this to 513).
    private const int StackSize = 513;
    private readonly double[] _stack = new double[StackSize];
    private int _sp;

    private readonly CffIndex _globalSubrs;
    private readonly CffIndex _localSubrs;
    private readonly int _globalBias;
    private readonly int _localBias;
    private readonly IGlyphSink _sink;

    private float _x;
    private float _y;
    private float _startX;
    private float _startY;
    private bool _contourOpen;
    private int _stemCount;
    private bool _widthDiscarded;
    private bool _terminated;       // endchar — abort all enclosing charstrings
    private bool _returnFromSubr;   // op 11 — exit current charstring only
    private readonly SeacResolver? _seac;

    /// <summary>
    /// Draws an accented composite: the glyph StandardEncoding assigns to <paramref name="baseCode"/>, and the one
    /// it assigns to <paramref name="accentCode"/> moved by (<paramref name="adx"/>, <paramref name="ady"/>), both
    /// into the composite's own sink (TN #5177 Appendix C, <c>endchar</c> with four operands).
    /// </summary>
    public delegate void SeacResolver(float adx, float ady, int baseCode, int accentCode);

    public static void Execute(ReadOnlyMemory<byte> charstring,
        CffIndex globalSubrs, CffIndex localSubrs, IGlyphSink sink, SeacResolver? seac = null)
    {
        var i = new Type2CharstringInterpreter(globalSubrs, localSubrs, sink, seac);
        i.RunCharstring(charstring.Span);
        if (i._contourOpen) sink.Close();
    }

    private Type2CharstringInterpreter(CffIndex gsubr, CffIndex lsubr, IGlyphSink sink, SeacResolver? seac)
    {
        _globalSubrs = gsubr;
        _localSubrs = lsubr;
        _globalBias = ComputeBias(gsubr.Count);
        _localBias = ComputeBias(lsubr.Count);
        _sink = sink;
        _seac = seac;
    }

    private static int ComputeBias(int count)
        => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private void RunCharstring(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && !_terminated && !_returnFromSubr)
        {
            var b0 = data[i];
            if (b0 >= 32 || b0 == 28)
            {
                // Operand.
                i = ReadOperand(data, i);
            }
            else
            {
                // Operator.
                if (b0 == 12)
                {
                    var ext = data[i + 1];
                    i += 2;
                    HandleExtOp(ext);
                }
                else
                {
                    i++;
                    HandleOp(b0, data, ref i);
                }
            }
        }
    }

    private int ReadOperand(ReadOnlySpan<byte> data, int i)
    {
        var b0 = data[i];
        double v;
        int len;
        if (b0 == 28)
        {
            v = (short)((data[i + 1] << 8) | data[i + 2]);
            len = 3;
        }
        else if (b0 is >= 32 and <= 246)
        {
            v = b0 - 139;
            len = 1;
        }
        else if (b0 is >= 247 and <= 250)
        {
            v = (b0 - 247) * 256 + data[i + 1] + 108;
            len = 2;
        }
        else if (b0 is >= 251 and <= 254)
        {
            v = -((b0 - 251) * 256) - data[i + 1] - 108;
            len = 2;
        }
        else // 255: 16.16 fixed
        {
            var raw = (data[i + 1] << 24) | (data[i + 2] << 16)
                    | (data[i + 3] << 8)  |  data[i + 4];
            v = raw / 65536.0;
            len = 5;
        }
        if (_sp >= StackSize)
            throw new InvalidDataException("Type-2: operand stack overflow");
        _stack[_sp++] = v;
        return i + len;
    }

    private void HandleOp(byte op, ReadOnlySpan<byte> data, ref int i)
    {
        switch (op)
        {
            // ---- Hint operators ---------------------------------------------
            case 1:  // hstem
            case 3:  // vstem
            case 18: // hstemhm
            case 23: // vstemhm
                DiscardWidthBeforeHints();
                _stemCount += _sp / 2;
                _sp = 0;
                break;
            case 19: // hintmask
            case 20: // cntrmask
                DiscardWidthBeforeHints();
                // Implicit: any pending stems on stack count as hstem-equivalent.
                _stemCount += _sp / 2;
                _sp = 0;
                {
                    var maskBytes = (_stemCount + 7) >> 3;
                    i += maskBytes;
                }
                break;

            // ---- Move-tos ---------------------------------------------------
            case 21: // rmoveto
                DiscardWidthIfExtra(2);
                MoveTo(_x + (float)_stack[_sp - 2], _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;
            case 22: // hmoveto
                DiscardWidthIfExtra(1);
                MoveTo(_x + (float)_stack[_sp - 1], _y);
                _sp = 0;
                break;
            case 4:  // vmoveto
                DiscardWidthIfExtra(1);
                MoveTo(_x, _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;

            // ---- Line-tos ---------------------------------------------------
            case 5: // rlineto: {dxa dya}+
                for (var k = 0; k + 1 < _sp; k += 2)
                    LineTo(_x + (float)_stack[k], _y + (float)_stack[k + 1]);
                _sp = 0;
                break;
            case 6: // hlineto: dx1 {dya dxb}* OR {dxa dyb}+
                for (var k = 0; k < _sp; k++)
                {
                    if ((k & 1) == 0) LineTo(_x + (float)_stack[k], _y);
                    else              LineTo(_x, _y + (float)_stack[k]);
                }
                _sp = 0;
                break;
            case 7: // vlineto
                for (var k = 0; k < _sp; k++)
                {
                    if ((k & 1) == 0) LineTo(_x, _y + (float)_stack[k]);
                    else              LineTo(_x + (float)_stack[k], _y);
                }
                _sp = 0;
                break;

            // ---- Curve-tos --------------------------------------------------
            case 8: // rrcurveto: {dxa dya dxb dyb dxc dyc}+
                for (var k = 0; k + 5 < _sp; k += 6)
                    EmitRRCurve(_stack[k], _stack[k + 1], _stack[k + 2],
                                _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                _sp = 0;
                break;
            case 24: // rcurveline: {6}+ 2
                {
                    var k = 0;
                    while (k + 5 < _sp - 2)
                    {
                        EmitRRCurve(_stack[k], _stack[k + 1], _stack[k + 2],
                                    _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                        k += 6;
                    }
                    LineTo(_x + (float)_stack[_sp - 2], _y + (float)_stack[_sp - 1]);
                    _sp = 0;
                }
                break;
            case 25: // rlinecurve: {2}+ 6
                {
                    var k = 0;
                    while (k + 1 < _sp - 6)
                    {
                        LineTo(_x + (float)_stack[k], _y + (float)_stack[k + 1]);
                        k += 2;
                    }
                    EmitRRCurve(_stack[_sp - 6], _stack[_sp - 5], _stack[_sp - 4],
                                _stack[_sp - 3], _stack[_sp - 2], _stack[_sp - 1]);
                    _sp = 0;
                }
                break;
            case 26: // vvcurveto: dx1? {dya dxb dyb dyc}+
                {
                    var k = 0;
                    var dx = 0.0;
                    if ((_sp & 1) != 0) { dx = _stack[k++]; }
                    while (k + 3 < _sp + 1 && k + 3 < _sp)
                    {
                        EmitRRCurve(dx, _stack[k], _stack[k + 1], _stack[k + 2],
                                    0, _stack[k + 3]);
                        dx = 0;
                        k += 4;
                    }
                    _sp = 0;
                }
                break;
            case 27: // hhcurveto: dy1? {dxa dxb dyb dxc}+
                {
                    var k = 0;
                    var dy = 0.0;
                    if ((_sp & 1) != 0) { dy = _stack[k++]; }
                    while (k + 3 < _sp)
                    {
                        EmitRRCurve(_stack[k], dy, _stack[k + 1], _stack[k + 2],
                                    _stack[k + 3], 0);
                        dy = 0;
                        k += 4;
                    }
                    _sp = 0;
                }
                break;
            case 30: // vhcurveto
                EmitAlternatingCurves(startsHoriz: false);
                _sp = 0;
                break;
            case 31: // hvcurveto
                EmitAlternatingCurves(startsHoriz: true);
                _sp = 0;
                break;

            // ---- Subroutines ------------------------------------------------
            case 10: // callsubr (local)
                {
                    var sn = (int)_stack[--_sp] + _localBias;
                    if (sn < 0 || sn >= _localSubrs.Count)
                        throw new InvalidDataException($"Type-2: local subr {sn} out of range");
                    RunCharstring(_localSubrs.GetObject(sn));
                    _returnFromSubr = false; // pop the exit flag once we're back in caller
                }
                break;
            case 29: // callgsubr (global)
                {
                    var sn = (int)_stack[--_sp] + _globalBias;
                    if (sn < 0 || sn >= _globalSubrs.Count)
                        throw new InvalidDataException($"Type-2: global subr {sn} out of range");
                    RunCharstring(_globalSubrs.GetObject(sn));
                    _returnFromSubr = false;
                }
                break;
            case 11: // return — exit current subroutine, caller continues
                _returnFromSubr = true;
                break;

            // ---- Termination ------------------------------------------------
            case 14: // endchar
                // An odd count carries the width first: 1 (width) or 5 (width + the four seac operands).
                if (!_widthDiscarded && (_sp == 1 || _sp == 5))
                {
                    for (var k = 0; k < _sp - 1; k++) _stack[k] = _stack[k + 1];
                    _sp--;
                }
                _widthDiscarded = true;
                // "adx ady bchar achar endchar" is an accented composite, the Type 1 seac carried over (TN #5177
                // Appendix C). Fonts converted from Type 1 build their accented letters this way: a German subset
                // set ä, ö and ü like that, and with the operands dropped all three drew nothing.
                if (_sp == 4 && _seac is not null)
                {
                    if (_contourOpen)
                    {
                        _sink.Close();
                        _contourOpen = false;
                    }
                    _seac((float)_stack[0], (float)_stack[1], (int)_stack[2], (int)_stack[3]);
                }
                _sp = 0;
                _terminated = true;
                break;

            // Reserved / unhandled: silently skip.
            default:
                _sp = 0;
                break;
        }
    }

    // The transient array of TN #5177 §4.4 (put / get): 32 elements in CFF1.
    private readonly double[] _transient = new double[32];

    private void HandleExtOp(byte ext)
    {
        switch (ext)
        {
            // ---- Flex (TN #5177 §4.1) ---------------------------------------
            // Each is two curves that MOVE the current point. Skipping them (as this once did, on the theory that
            // they were rare) left the pen behind, so every point after a flex in the glyph was drawn displaced by
            // the flex's travel: fonts converted from Type 1 use flex throughout (serifs, stem joins), and a whole
            // CFF subset (NewBaskerville in C# in Depth) drew its capitals and half its lowercase as smears. The
            // flex depth (fd) only says when a renderer MAY flatten the pair to a line; drawing the curves is exact.
            case 35: // flex: dx1 dy1 dx2 dy2 dx3 dy3 dx4 dy4 dx5 dy5 dx6 dy6 fd
                if (_sp >= 13)
                {
                    EmitRRCurve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    EmitRRCurve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                }
                _sp = 0;
                break;
            case 34: // hflex: dx1 dx2 dy2 dx3 dx4 dx5 dx6 (both curves start and end on one y)
                if (_sp >= 7)
                {
                    EmitRRCurve(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                    EmitRRCurve(_stack[4], 0, _stack[5], -_stack[2], _stack[6], 0);
                }
                _sp = 0;
                break;
            case 36: // hflex1: dx1 dy1 dx2 dy2 dx3 dx4 dx5 dy5 dx6 (ends on the starting y)
                if (_sp >= 9)
                {
                    EmitRRCurve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                    EmitRRCurve(_stack[5], 0, _stack[6], _stack[7], _stack[8],
                        -(_stack[1] + _stack[3] + _stack[7]));
                }
                _sp = 0;
                break;
            case 37: // flex1: dx1 dy1 ... dx5 dy5 d6
                if (_sp >= 11)
                {
                    // d6 is the last point's coordinate along whichever axis the first five points travel further;
                    // along the other the curve returns to where it started.
                    double dx = 0, dy = 0;
                    for (var k = 0; k < 10; k += 2) { dx += _stack[k]; dy += _stack[k + 1]; }
                    double dx6, dy6;
                    if (Math.Abs(dx) > Math.Abs(dy)) { dx6 = _stack[10]; dy6 = -dy; }
                    else { dx6 = -dx; dy6 = _stack[10]; }
                    EmitRRCurve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    EmitRRCurve(_stack[6], _stack[7], _stack[8], _stack[9], dx6, dy6);
                }
                _sp = 0;
                break;

            // ---- Arithmetic, logic and storage (TN #5177 §4.3-4.4) ------------
            // These work ON the stack and leave their result there, so clearing it (as before) also threw away the
            // operands a later path operator was about to consume.
            case 3: Binary((a, b) => a != 0 && b != 0 ? 1 : 0); break;  // and
            case 4: Binary((a, b) => a != 0 || b != 0 ? 1 : 0); break;  // or
            case 5: Unary(a => a == 0 ? 1 : 0); break;                   // not
            case 9: Unary(Math.Abs); break;                               // abs
            case 10: Binary((a, b) => a + b); break;                      // add
            case 11: Binary((a, b) => a - b); break;                      // sub
            case 12: Binary((a, b) => b == 0 ? 0 : a / b); break;         // div
            case 14: Unary(a => -a); break;                               // neg
            case 15: Binary((a, b) => a == b ? 1 : 0); break;             // eq
            case 18: if (_sp > 0) _sp--; break;                           // drop
            case 20: // put: val i
                if (_sp >= 2)
                {
                    var i = (int)_stack[_sp - 1];
                    if ((uint)i < (uint)_transient.Length) _transient[i] = _stack[_sp - 2];
                    _sp -= 2;
                }
                break;
            case 21: // get: i -> val
                if (_sp >= 1)
                {
                    var i = (int)_stack[_sp - 1];
                    _stack[_sp - 1] = (uint)i < (uint)_transient.Length ? _transient[i] : 0;
                }
                break;
            case 22: // ifelse: s1 s2 v1 v2 -> v1 <= v2 ? s1 : s2
                if (_sp >= 4)
                {
                    var v2 = _stack[_sp - 1];
                    var v1 = _stack[_sp - 2];
                    var s2 = _stack[_sp - 3];
                    var s1 = _stack[_sp - 4];
                    _sp -= 3;
                    _stack[_sp - 1] = v1 <= v2 ? s1 : s2;
                }
                break;
            case 23: // random: a value in (0, 1]. Fixed rather than random, so a glyph draws the same every time
                // and two renders of one page agree.
                if (_sp < StackSize) _stack[_sp++] = 0.5;
                break;
            case 24: Binary((a, b) => a * b); break;                       // mul
            case 26: Unary(a => Math.Sqrt(Math.Max(a, 0))); break;         // sqrt
            case 27: if (_sp > 0 && _sp < StackSize) { _stack[_sp] = _stack[_sp - 1]; _sp++; } break; // dup
            case 28: // exch
                if (_sp >= 2) (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                break;
            case 29: // index: i -> copy of the element i below it (i < 0: the top one)
                if (_sp >= 1)
                {
                    var i = (int)_stack[_sp - 1];
                    if (i < 0) i = 0;
                    var from = _sp - 2 - i;
                    _stack[_sp - 1] = from >= 0 ? _stack[from] : 0;
                }
                break;
            case 30: // roll: N J -> the top N elements rolled J places toward the top
                if (_sp >= 2)
                {
                    var j = (int)_stack[_sp - 1];
                    var n = (int)_stack[_sp - 2];
                    _sp -= 2;
                    if (n > 0 && n <= _sp)
                    {
                        var start = _sp - n;
                        j %= n;
                        if (j < 0) j += n;
                        if (j != 0)
                        {
                            Span<double> tmp = stackalloc double[n];
                            for (var k = 0; k < n; k++) tmp[(k + j) % n] = _stack[start + k];
                            tmp.CopyTo(_stack.AsSpan(start, n));
                        }
                    }
                }
                break;

            // Reserved (and the Type 1 dotsection, 12 0): consume nothing meaningful.
            default:
                _sp = 0;
                break;
        }
    }

    private void Unary(Func<double, double> f)
    {
        if (_sp >= 1) _stack[_sp - 1] = f(_stack[_sp - 1]);
    }

    private void Binary(Func<double, double, double> f)
    {
        if (_sp < 2) return;
        _stack[_sp - 2] = f(_stack[_sp - 2], _stack[_sp - 1]);
        _sp--;
    }

    // ---- Helpers -----------------------------------------------------------

    private void EmitAlternatingCurves(bool startsHoriz)
    {
        var k = 0;
        var horiz = startsHoriz;
        while (k + 3 < _sp)
        {
            // 4 args per curve, optional final dxf/dyf if 5 args remain.
            double dxa, dya, dxb, dyb, dxc, dyc;
            if (horiz)
            {
                // hvcurveto-style: dx1 dx2 dy2 dy3
                dxa = _stack[k];     dya = 0;
                dxb = _stack[k + 1]; dyb = _stack[k + 2];
                dxc = 0;             dyc = _stack[k + 3];
                if (_sp - k == 5) { dxc = _stack[k + 4]; }
            }
            else
            {
                dxa = 0;             dya = _stack[k];
                dxb = _stack[k + 1]; dyb = _stack[k + 2];
                dxc = _stack[k + 3]; dyc = 0;
                if (_sp - k == 5) { dyc = _stack[k + 4]; }
            }
            EmitRRCurve(dxa, dya, dxb, dyb, dxc, dyc);
            k += 4;
            if (_sp - k == 1) k++; // consume the final extra
            horiz = !horiz;
        }
    }

    private void EmitRRCurve(double dxa, double dya, double dxb, double dyb,
        double dxc, double dyc)
    {
        var c1x = _x + (float)dxa;
        var c1y = _y + (float)dya;
        var c2x = c1x + (float)dxb;
        var c2y = c1y + (float)dyb;
        var nx = c2x + (float)dxc;
        var ny = c2y + (float)dyc;
        _sink.CubicTo(c1x, c1y, c2x, c2y, nx, ny);
        _x = nx;
        _y = ny;
    }

    private void MoveTo(float x, float y)
    {
        if (_contourOpen) _sink.Close();
        _x = x;
        _y = y;
        _startX = x;
        _startY = y;
        _sink.MoveTo(x, y);
        _contourOpen = true;
    }

    private void LineTo(float x, float y)
    {
        _sink.LineTo(x, y);
        _x = x;
        _y = y;
    }

    private void DiscardWidthBeforeHints()
    {
        // hstem/vstem etc. expect an even arg count. Odd → leftmost is width.
        if (_widthDiscarded) return;
        if ((_sp & 1) != 0)
        {
            // Shift left by one (drop bottom).
            for (var k = 0; k < _sp - 1; k++) _stack[k] = _stack[k + 1];
            _sp--;
        }
        _widthDiscarded = true;
    }

    private void DiscardWidthIfExtra(int expectedArgs)
    {
        if (_widthDiscarded) return;
        if (_sp == expectedArgs + 1)
        {
            for (var k = 0; k < _sp - 1; k++) _stack[k] = _stack[k + 1];
            _sp--;
        }
        _widthDiscarded = true;
    }

}
