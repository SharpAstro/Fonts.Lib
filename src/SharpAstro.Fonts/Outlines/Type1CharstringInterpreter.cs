namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// PostScript Type 1 charstring interpreter. Decodes Adobe Type 1 glyph
/// procedures into outline path commands emitted to an
/// <see cref="IGlyphSink"/>.
///
/// <para>Spec: Adobe Type 1 Font Format (the "black book"), §6.4
/// "Charstring Encoding".</para>
///
/// <para>Per-call instance state (operand stack, current point,
/// transient subroutine map) lives on the calling stack; never escapes.
/// Safe to invoke concurrently from any thread.</para>
///
/// <para>Implemented: all path operators + callsubr / return / endchar /
/// hsbw / sbw / closepath / hstem / vstem / dotsection / hstem3 / vstem3.
/// Hint operators only consume args. <c>seac</c> (accented composite)
/// throws via the <see cref="SeacResolver"/> callback so the caller can
/// recursively render the base + accent glyphs. <c>callothersubr</c>,
/// <c>pop</c>, <c>div</c>, <c>setcurrentpoint</c> handled as best-effort.</para>
/// </summary>
internal sealed class Type1CharstringInterpreter
{
    private const int StackSize = 24; // Type 1 spec: max 24 operand stack
    private readonly double[] _stack = new double[StackSize];
    private int _sp;

    private readonly byte[][] _localSubrs;
    private readonly IGlyphSink _sink;

    private float _x;
    private float _y;
    private float _startX;
    private float _startY;
    private bool _contourOpen;
    private bool _terminated;

    // OtherSubr scaffolding. callothersubr moves operands to the PostScript stack and pop reads them
    // back; without this, flex (OtherSubrs 0/1/2) draws its 7 rmovetos as stray strokes and hint
    // replacement (OtherSubr 3) calls the wrong subr — garbling any font that uses them (most non-CM
    // Type 1 faces do). See "The Type 1 Font Format" §8 (Flex and Hint Replacement).
    private readonly double[] _psStack = new double[StackSize];
    private int _psSp;
    private bool _flexing;
    private readonly (float X, float Y)[] _flexPts = new (float, float)[8];
    private int _flexCount;

    public delegate void SeacResolver(int adx, int ady, string baseChar, string accentChar);
    private readonly SeacResolver? _seac;

    public static void Execute(byte[] charstring, byte[][] localSubrs, IGlyphSink sink,
        SeacResolver? seac = null)
    {
        var i = new Type1CharstringInterpreter(localSubrs, sink, seac);
        i.RunCharstring(charstring);
        if (i._contourOpen) sink.Close();
    }

    private Type1CharstringInterpreter(byte[][] localSubrs, IGlyphSink sink, SeacResolver? seac)
    {
        _localSubrs = localSubrs;
        _sink = sink;
        _seac = seac;
    }

    private void RunCharstring(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && !_terminated)
        {
            var b0 = data[i];
            if (b0 >= 32 || b0 == 255)
            {
                i = ReadOperand(data, i);
            }
            else
            {
                if (b0 == 12)
                {
                    var ext = data[i + 1];
                    i += 2;
                    HandleExtOp(ext);
                }
                else
                {
                    i++;
                    HandleOp(b0);
                }
            }
        }
    }

    private int ReadOperand(ReadOnlySpan<byte> data, int i)
    {
        var b0 = data[i];
        double v;
        int len;
        if (b0 is >= 32 and <= 246)
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
        else // 255: 32-bit signed
        {
            var raw = (data[i + 1] << 24) | (data[i + 2] << 16)
                    | (data[i + 3] << 8)  |  data[i + 4];
            v = raw;
            len = 5;
        }
        if (_sp >= StackSize) _sp = StackSize - 1; // saturate; defensive
        _stack[_sp++] = v;
        return i + len;
    }

    private void HandleOp(byte op)
    {
        switch (op)
        {
            case 1:  // hstem (y dy)
            case 3:  // vstem (x dx)
                _sp = 0;
                break;
            case 4:  // vmoveto (dy)
                DoMove(_x, _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;
            case 5:  // rlineto (dx dy)
                LineTo(_x + (float)_stack[_sp - 2], _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;
            case 6:  // hlineto (dx)
                LineTo(_x + (float)_stack[_sp - 1], _y);
                _sp = 0;
                break;
            case 7:  // vlineto (dy)
                LineTo(_x, _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;
            case 8:  // rrcurveto (dx1 dy1 dx2 dy2 dx3 dy3)
                EmitCubic(_stack[_sp - 6], _stack[_sp - 5], _stack[_sp - 4],
                          _stack[_sp - 3], _stack[_sp - 2], _stack[_sp - 1]);
                _sp = 0;
                break;
            case 9:  // closepath — closes the subpath but, per the Type 1 spec, leaves the current point
                     // UNCHANGED (unlike PostScript closepath, which repositions it to the start).
                     // Resetting it to the start displaced every following rmoveto-addressed contour
                     // (the i/j dot, accent marks, …) by the start→last-point delta — floating blobs.
                if (_contourOpen)
                {
                    _sink.Close();
                    _contourOpen = false;
                }
                _sp = 0;
                break;
            case 10: // callsubr (n)
                {
                    var sn = (int)_stack[--_sp];
                    if ((uint)sn < (uint)_localSubrs.Length && _localSubrs[sn] is { } sub)
                        RunCharstring(sub);
                }
                break;
            case 11: // return
                // Caller's loop continues; nothing to do here (we don't unwind further).
                break;
            case 13: // hsbw (sbx wx)
                {
                    var sbx = (float)_stack[0];
                    // wx = stack[1] — advance width, ignored at this layer.
                    _x = sbx;
                    _y = 0;
                    _startX = _x;
                    _startY = _y;
                    _sp = 0;
                }
                break;
            case 14: // endchar
                _terminated = true;
                _sp = 0;
                break;
            case 21: // rmoveto (dx dy)
                DoMove(_x + (float)_stack[_sp - 2], _y + (float)_stack[_sp - 1]);
                _sp = 0;
                break;
            case 22: // hmoveto (dx)
                DoMove(_x + (float)_stack[_sp - 1], _y);
                _sp = 0;
                break;
            case 30: // vhcurveto (dy1 dx2 dy2 dx3)
                EmitCubic(0, _stack[_sp - 4], _stack[_sp - 3], _stack[_sp - 2], _stack[_sp - 1], 0);
                _sp = 0;
                break;
            case 31: // hvcurveto (dx1 dx2 dy2 dy3)
                EmitCubic(_stack[_sp - 4], 0, _stack[_sp - 3], _stack[_sp - 2], 0, _stack[_sp - 1]);
                _sp = 0;
                break;
            default:
                _sp = 0;
                break;
        }
    }

    private void HandleExtOp(byte ext)
    {
        switch (ext)
        {
            case 0:  // dotsection — hint
            case 1:  // vstem3
            case 2:  // hstem3
                _sp = 0;
                break;
            case 6:  // seac (asb adx ady bchar achar)
                if (_seac is not null && _sp >= 5)
                    _seac((int)_stack[1], (int)_stack[2],
                          GlyphNameLookup((int)_stack[3]),
                          GlyphNameLookup((int)_stack[4]));
                _terminated = true; // seac always terminates the current charstring
                _sp = 0;
                break;
            case 7:  // sbw (sbx sby wx wy) — set both bearings + advances
                {
                    var sbx = (float)_stack[0];
                    var sby = (float)_stack[1];
                    _x = sbx;
                    _y = sby;
                    _startX = _x;
                    _startY = _y;
                    _sp = 0;
                }
                break;
            case 12: // div (a b)
                {
                    var b = _stack[--_sp];
                    var a = _stack[--_sp];
                    _stack[_sp++] = b != 0 ? a / b : 0;
                }
                break;
            case 16: // callothersubr (arg1 … argn n othersubr#)
                CallOtherSubr();
                break;
            case 17: // pop — move one value back from the PostScript stack to the operand stack
                if (_sp < StackSize) _stack[_sp++] = _psSp > 0 ? _psStack[--_psSp] : 0;
                break;
            case 33: // setcurrentpoint (x y) — used post-othersubr; absolute set
                _x = (float)_stack[_sp - 2];
                _y = (float)_stack[_sp - 1];
                _sp = 0;
                break;
            default:
                _sp = 0;
                break;
        }
    }

    // Handle `callothersubr`. The operand stack holds `arg1 … argn n othersubr#` (top = othersubr#).
    // OtherSubrs 0–3 are the standard flex / hint-replacement helpers; their results are left on the
    // PostScript stack for subsequent `pop`s. Unknown OtherSubrs pass their args straight through.
    private void CallOtherSubr()
    {
        var subrNo = _sp > 0 ? (int)_stack[--_sp] : 0;
        var nArgs = _sp > 0 ? (int)_stack[--_sp] : 0;
        if (nArgs < 0) nArgs = 0;
        if (nArgs > _sp) nArgs = _sp;

        switch (subrNo)
        {
            case 1: // start flex — suppress the 7 rmovetos that follow; record them as control points
                _flexing = true;
                _flexCount = 0;
                _sp -= nArgs;
                break;
            case 2: // flex point marker — the position was captured by the suppressed rmoveto
                _sp -= nArgs;
                break;
            case 0: // end flex (args: flex-height end-x end-y) — emit the two collected curves
                if (_flexCount >= 7)
                {
                    EmitCubicAbs(_flexPts[1], _flexPts[2], _flexPts[3]);
                    EmitCubicAbs(_flexPts[4], _flexPts[5], _flexPts[6]);
                }
                _flexing = false;
                // Return end-x/end-y for the following `pop pop setcurrentpoint` (pop yields x first).
                var endY = nArgs >= 1 ? _stack[_sp - 1] : _y;
                var endX = nArgs >= 2 ? _stack[_sp - 2] : _x;
                _sp -= nArgs;
                PsPush(endY);
                PsPush(endX);
                break;
            case 3: // hint replacement — return the subr number for the following `pop callsubr`
                var sn = nArgs >= 1 ? _stack[_sp - 1] : 3;
                _sp -= nArgs;
                PsPush(sn);
                break;
            default: // unknown — pass args through so subsequent pops retrieve them in order
                for (var k = 0; k < nArgs; k++) PsPush(_stack[_sp - 1 - k]);
                _sp -= nArgs;
                break;
        }
    }

    private void PsPush(double v)
    {
        if (_psSp < _psStack.Length) _psStack[_psSp++] = v;
    }

    // A moveto: starts a new contour, except mid-flex where the 7 movetos are control points
    // appended to the current contour (recorded, not emitted).
    private void DoMove(float x, float y)
    {
        if (_flexing)
        {
            _x = x;
            _y = y;
            if (_flexCount < _flexPts.Length) _flexPts[_flexCount++] = (x, y);
            return;
        }
        MoveTo(x, y);
    }

    private void EmitCubicAbs((float X, float Y) c1, (float X, float Y) c2, (float X, float Y) end)
    {
        _sink.CubicTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
        _x = end.X;
        _y = end.Y;
    }

    private void EmitCubic(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        var c1x = _x + (float)dx1;
        var c1y = _y + (float)dy1;
        var c2x = c1x + (float)dx2;
        var c2y = c1y + (float)dy2;
        var nx = c2x + (float)dx3;
        var ny = c2y + (float)dy3;
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

    /// <summary>Adobe Standard Encoding glyph-name lookup for seac base/accent indices.</summary>
    private static string GlyphNameLookup(int code) => code switch
    {
        // seac uses Adobe Standard Encoding indices for the accent + base.
        // Full table not needed here — caller will fall back to ".notdef"
        // for unknowns. We can extend this in a follow-up.
        65 => "A", 66 => "B", 67 => "C", 68 => "D", 69 => "E", 70 => "F",
        71 => "G", 72 => "H", 73 => "I", 74 => "J", 75 => "K", 76 => "L",
        77 => "M", 78 => "N", 79 => "O", 80 => "P", 81 => "Q", 82 => "R",
        83 => "S", 84 => "T", 85 => "U", 86 => "V", 87 => "W", 88 => "X",
        89 => "Y", 90 => "Z",
        97 => "a", 98 => "b", 99 => "c", 100 => "d", 101 => "e", 102 => "f",
        103 => "g", 104 => "h", 105 => "i", 106 => "j", 107 => "k", 108 => "l",
        109 => "m", 110 => "n", 111 => "o", 112 => "p", 113 => "q", 114 => "r",
        115 => "s", 116 => "t", 117 => "u", 118 => "v", 119 => "w", 120 => "x",
        121 => "y", 122 => "z",
        _ => ".notdef",
    };
}
