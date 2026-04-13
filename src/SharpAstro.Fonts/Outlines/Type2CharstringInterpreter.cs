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
/// no bytecode hinting (Phase 8). Math / conditional / flex extended
/// operators are silently skipped — they are vanishingly rare in real fonts.</para>
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

    public static void Execute(ReadOnlyMemory<byte> charstring,
        CffIndex globalSubrs, CffIndex localSubrs, IGlyphSink sink)
    {
        var i = new Type2CharstringInterpreter(globalSubrs, localSubrs, sink);
        i.RunCharstring(charstring.Span);
        if (i._contourOpen) sink.Close();
    }

    private Type2CharstringInterpreter(CffIndex gsubr, CffIndex lsubr, IGlyphSink sink)
    {
        _globalSubrs = gsubr;
        _localSubrs = lsubr;
        _globalBias = ComputeBias(gsubr.Count);
        _localBias = ComputeBias(lsubr.Count);
        _sink = sink;
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
                DiscardWidthIfExtra(0);
                _sp = 0;
                _terminated = true;
                break;

            // Reserved / unhandled: silently skip.
            default:
                _sp = 0;
                break;
        }
    }

    private void HandleExtOp(byte ext)
    {
        // Math / conditional / flex ops — we don't need them for the
        // overwhelming majority of glyphs. Silently consume operands.
        // (Emitting flex curves correctly is a quality improvement we can
        // come back to once the basic interpreter is proven.)
        _sp = 0;
        _ = ext;
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
