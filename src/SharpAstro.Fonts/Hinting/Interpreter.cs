namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// TrueType bytecode interpreter. <b>Phase 8 foundation — incomplete.</b>
///
/// <para>Implements the dispatch loop, operand stack, function table,
/// graphics state, and ~60 of the ~150 v40 opcodes. Missing opcodes
/// are silently treated as no-ops (operand stack is left intact); that's
/// safe for execution but produces incorrect hinted output until they're
/// filled in.</para>
///
/// <para>One instance per face. <c>fpgm</c> runs once via <see cref="RunFpgm"/>.
/// <c>prep</c> runs on every size change via <see cref="RunPrep"/>. Per-glyph
/// instructions run via <see cref="RunGlyphProgram"/>.</para>
///
/// <para><b>Thread-safety:</b> instances are NOT safe for concurrent use —
/// each glyph render needs its own copy of the per-glyph mutable state
/// (zones, GS). The face-level state (function table, CVT, Storage Area)
/// is set up once at face load and read-only thereafter. A pooling /
/// snapshot strategy goes in once we have the whole interpreter working.</para>
/// </summary>
internal sealed class Interpreter
{
    // ---- Stack -----------------------------------------------------------
    private readonly int[] _stack;
    private int _sp;

    // ---- Storage / CVT --------------------------------------------------
    private readonly int[] _storage;       // per face, persists across runs
    private int[] _cvt;                    // F26.6, scaled to current ppem
    private readonly ushort[] _cvtFunits;  // raw FUnit values from 'cvt ' table

    // ---- Function table -------------------------------------------------
    private readonly Function[] _functions;

    // ---- Per-glyph state ------------------------------------------------
    private GraphicsState _gs;
    private readonly Zone _twilight;
    private Zone _glyph = null!;

    // ---- Scaling --------------------------------------------------------
    private float _ppem;
    private int _scale_26_6; // pixels per font-unit, F26.6

    private const int MaxFunctionId = 256;

    public Interpreter(ushort maxStackElements, ushort maxStorage,
        ushort maxFunctionDefs, ushort maxTwilightPoints,
        ushort[] cvtFunits)
    {
        _stack = new int[Math.Max(256, (int)maxStackElements)];
        _storage = new int[Math.Max(64, (int)maxStorage)];
        _functions = new Function[Math.Max(64, (int)maxFunctionDefs)];
        _twilight = new Zone(Math.Max(16, (int)maxTwilightPoints));
        _cvtFunits = cvtFunits;
        _cvt = new int[cvtFunits.Length];
        _gs = GraphicsState.Default;
    }

    /// <summary>Run the font program (fpgm) — defines functions reused later.</summary>
    public void RunFpgm(byte[] fpgm)
    {
        if (fpgm.Length == 0) return;
        Execute(fpgm);
    }

    /// <summary>Re-scale CVT to <paramref name="ppem"/>, then run prep.</summary>
    public void OnSizeChange(float ppem, int unitsPerEm, byte[] prep)
    {
        _ppem = ppem;
        _scale_26_6 = (int)MathF.Round(ppem * 64f / unitsPerEm * 64f); // ppem px / upem font-units, in F26.6
        // CVT values are in font units; scale to pixels.
        for (var i = 0; i < _cvtFunits.Length; i++)
            _cvt[i] = ScaleFunits(_cvtFunits[i]);
        // Reset graphics state to defaults (per spec) before prep.
        _gs = GraphicsState.Default;
        if (prep.Length > 0) Execute(prep);
    }

    /// <summary>
    /// Run a glyph's instruction stream over the given <paramref name="zone"/>
    /// (which should hold the glyph's points + 4 phantom points pre-loaded).
    /// </summary>
    public void RunGlyphProgram(byte[] instructions, Zone zone)
    {
        if (instructions.Length == 0) return;
        _glyph = zone;
        // Each glyph starts with default GS (per spec).
        _gs = GraphicsState.Default;
        Execute(instructions);
    }

    private int ScaleFunits(int funits) => (funits * _scale_26_6) >> 6;

    // ---- Dispatch loop ---------------------------------------------------

    private void Execute(byte[] code)
    {
        // Phase 8 foundation: if a hint program hits an unimplemented opcode
        // path that triggers an out-of-range, swallow the exception rather
        // than crash the glyph render. Hinted output will be wrong but
        // rendering proceeds. Remove this guard once opcode coverage is
        // complete.
        try
        {
            var ip = 0;
            while (ip < code.Length)
            {
                var op = code[ip++];
                ip = Dispatch(op, code, ip);
            }
        }
        catch (IndexOutOfRangeException) { /* malformed bytecode or missing opcode */ }
        catch (ArgumentOutOfRangeException) { /* same */ }
    }

    private int Dispatch(byte op, byte[] code, int ip)
    {
        // Bitfield-packed opcode families first.
        if (op is >= 0xB0 and <= 0xB7) // PUSHB[abc] — abc + 1 byte operands
        {
            var n = (op & 0x07) + 1;
            for (var i = 0; i < n; i++) Push(code[ip++]);
            return ip;
        }
        if (op is >= 0xB8 and <= 0xBF) // PUSHW[abc] — abc + 1 word operands
        {
            var n = (op & 0x07) + 1;
            for (var i = 0; i < n; i++)
            {
                short w = (short)((code[ip] << 8) | code[ip + 1]);
                Push(w);
                ip += 2;
            }
            return ip;
        }
        if (op is >= 0xC0 and <= 0xDF) { /* MDRP — TODO */ Pop(); return ip; }
        if (op is >= 0xE0)              { /* MIRP — TODO */ Pop(); Pop(); return ip; }

        switch ((Op)op)
        {
            // Stack manipulation
            case Op.DUP:    { var v = Peek(); Push(v); break; }
            case Op.POP:    Pop(); break;
            case Op.CLEAR:  _sp = 0; break;
            case Op.SWAP:   { var a = Pop(); var b = Pop(); Push(a); Push(b); break; }
            case Op.DEPTH:  Push(_sp); break;
            case Op.CINDEX:
            {
                var k = Pop();
                Push(k > 0 && k <= _sp ? _stack[_sp - k] : 0);
                break;
            }
            case Op.MINDEX:
            {
                var k = Pop();
                if (k <= 0 || k > _sp) break; // defensive: malformed bytecode
                var v = _stack[_sp - k];
                for (var i = _sp - k; i < _sp - 1; i++) _stack[i] = _stack[i + 1];
                _stack[_sp - 1] = v;
                break;
            }
            case Op.ROLL:
            {
                if (_sp < 3) break; // defensive
                var c = _stack[_sp - 3];
                var b = _stack[_sp - 2];
                var a = _stack[_sp - 1];
                _stack[_sp - 3] = b; _stack[_sp - 2] = a; _stack[_sp - 1] = c;
                break;
            }

            // Push (count-prefixed)
            case Op.NPUSHB:
            {
                int n = code[ip++];
                for (var i = 0; i < n; i++) Push(code[ip++]);
                break;
            }
            case Op.NPUSHW:
            {
                int n = code[ip++];
                for (var i = 0; i < n; i++)
                {
                    Push((short)((code[ip] << 8) | code[ip + 1]));
                    ip += 2;
                }
                break;
            }

            // Storage / CVT
            case Op.RS:     { var i = Pop(); Push(i < _storage.Length ? _storage[i] : 0); break; }
            case Op.WS:     { var v = Pop(); var i = Pop(); if (i < _storage.Length) _storage[i] = v; break; }
            case Op.RCVT:   { var i = Pop(); Push(i < _cvt.Length ? _cvt[i] : 0); break; }
            case Op.WCVTP:  { var v = Pop(); var i = Pop(); if (i < _cvt.Length) _cvt[i] = v; break; }
            case Op.WCVTF:  { var v = Pop(); var i = Pop(); if (i < _cvt.Length) _cvt[i] = ScaleFunits(v); break; }

            // Arithmetic — all F26.6
            case Op.ADD:    { var b = Pop(); var a = Pop(); Push(a + b); break; }
            case Op.SUB:    { var b = Pop(); var a = Pop(); Push(a - b); break; }
            case Op.MUL:    { var b = Pop(); var a = Pop(); Push(F26Dot6.Mul(a, b)); break; }
            case Op.DIV:    { var b = Pop(); var a = Pop(); Push(F26Dot6.Div(a, b)); break; }
            case Op.NEG:    Push(-Pop()); break;
            case Op.ABS:    Push(Math.Abs(Pop())); break;
            case Op.MIN:    { var b = Pop(); var a = Pop(); Push(Math.Min(a, b)); break; }
            case Op.MAX:    { var b = Pop(); var a = Pop(); Push(Math.Max(a, b)); break; }
            case Op.FLOOR:  Push(Pop() & ~63); break;
            case Op.CEILING:{ var v = Pop(); Push((v + 63) & ~63); break; }

            // Logic
            case Op.LT:    { var b = Pop(); var a = Pop(); Push(a <  b ? 1 : 0); break; }
            case Op.LTEQ:  { var b = Pop(); var a = Pop(); Push(a <= b ? 1 : 0); break; }
            case Op.GT:    { var b = Pop(); var a = Pop(); Push(a >  b ? 1 : 0); break; }
            case Op.GTEQ:  { var b = Pop(); var a = Pop(); Push(a >= b ? 1 : 0); break; }
            case Op.EQ:    { var b = Pop(); var a = Pop(); Push(a == b ? 1 : 0); break; }
            case Op.NEQ:   { var b = Pop(); var a = Pop(); Push(a != b ? 1 : 0); break; }
            case Op.AND:   { var b = Pop(); var a = Pop(); Push((a != 0 && b != 0) ? 1 : 0); break; }
            case Op.OR:    { var b = Pop(); var a = Pop(); Push((a != 0 || b != 0) ? 1 : 0); break; }
            case Op.NOT:   Push(Pop() == 0 ? 1 : 0); break;
            case Op.ODD:   Push(((Pop() >> 6) & 1) != 0 ? 1 : 0); break;
            case Op.EVEN:  Push(((Pop() >> 6) & 1) == 0 ? 1 : 0); break;

            // Round modes
            case Op.RTG:   _gs.RoundState = 1; SetRound(64, 0, 32); break;
            case Op.RTHG:  _gs.RoundState = 0; SetRound(64, 32, 32); break;
            case Op.RTDG:  _gs.RoundState = 2; SetRound(32, 0, 16); break;
            case Op.RDTG:  _gs.RoundState = 6; SetRound(64, 0, 64); break;
            case Op.RUTG:  _gs.RoundState = 7; SetRound(64, 0, 0); break;
            case Op.ROFF:  _gs.RoundState = 5; break;

            // Graphics state setters
            case Op.SVTCA_x: _gs.ProjX = _gs.FreeX = _gs.DualX = 0x4000; _gs.ProjY = _gs.FreeY = _gs.DualY = 0; break;
            case Op.SVTCA_y: _gs.ProjX = _gs.FreeX = _gs.DualX = 0;      _gs.ProjY = _gs.FreeY = _gs.DualY = 0x4000; break;
            case Op.SPVTCA_x:_gs.ProjX = _gs.DualX = 0x4000; _gs.ProjY = _gs.DualY = 0; break;
            case Op.SPVTCA_y:_gs.ProjX = _gs.DualX = 0;      _gs.ProjY = _gs.DualY = 0x4000; break;
            case Op.SFVTCA_x:_gs.FreeX = 0x4000; _gs.FreeY = 0; break;
            case Op.SFVTCA_y:_gs.FreeX = 0;      _gs.FreeY = 0x4000; break;
            case Op.SRP0:    _gs.Rp0 = Pop(); break;
            case Op.SRP1:    _gs.Rp1 = Pop(); break;
            case Op.SRP2:    _gs.Rp2 = Pop(); break;
            case Op.SZP0:    _gs.Zp0 = (byte)Pop(); break;
            case Op.SZP1:    _gs.Zp1 = (byte)Pop(); break;
            case Op.SZP2:    _gs.Zp2 = (byte)Pop(); break;
            case Op.SZPS:    { var z = (byte)Pop(); _gs.Zp0 = _gs.Zp1 = _gs.Zp2 = z; break; }
            case Op.SLOOP:   _gs.Loop = Pop(); break;
            case Op.SMD:     _gs.MinimumDistance = Pop(); break;
            case Op.SCVTCI:  _gs.ControlValueCutIn = Pop(); break;
            case Op.SSWCI:   _gs.SingleWidthCutIn = Pop(); break;
            case Op.SSW:     _gs.SingleWidthValue = Pop(); break;
            case Op.SDB:     _gs.DeltaBase = (ushort)Pop(); break;
            case Op.SDS:     _gs.DeltaShift = (ushort)Pop(); break;
            case Op.FLIPON:  _gs.AutoFlip = true; break;
            case Op.FLIPOFF: _gs.AutoFlip = false; break;
            case Op.SCANCTRL:_gs.ScanControl = (ushort)Pop(); break;
            case Op.SCANTYPE:_gs.ScanType = (byte)Pop(); break;

            // Misc info
            case Op.MPPEM:   Push((int)MathF.Round(_ppem)); break;
            case Op.MPS:     Push((int)MathF.Round(_ppem)); break; // same as MPPEM in modern fonts
            case Op.GETINFO:
            {
                var selector = Pop();
                int result = 0;
                if ((selector & 1) != 0) result |= 40; // version 40 (FreeType-compatible)
                Push(result);
                break;
            }
            case Op.GPV:     Push(_gs.ProjX); Push(_gs.ProjY); break;
            case Op.GFV:     Push(_gs.FreeX); Push(_gs.FreeY); break;

            // Control flow
            case Op.IF:
            {
                var cond = Pop();
                if (cond == 0) ip = SkipToElseOrEif(code, ip);
                break;
            }
            case Op.ELSE:    ip = SkipToEif(code, ip); break;
            case Op.EIF:     break; // marker, no-op

            case Op.JMPR:    { var off = Pop(); ip = ip - 1 + off; break; }
            case Op.JROT:    { var cond = Pop(); var off = Pop(); if (cond != 0) ip = ip - 1 + off; break; }
            case Op.JROF:    { var cond = Pop(); var off = Pop(); if (cond == 0) ip = ip - 1 + off; break; }

            // Function defs
            case Op.FDEF:
            {
                var fid = Pop();
                var bodyStart = ip;
                while (ip < code.Length && code[ip] != (byte)Op.ENDF) ip++;
                if (fid >= 0 && fid < _functions.Length)
                    _functions[fid] = new Function(code, bodyStart, ip - bodyStart);
                ip++; // skip ENDF
                break;
            }
            case Op.IDEF:
            {
                // Instruction definition — overrides an unused opcode. Rare;
                // skip body for now (still consumes the ID).
                Pop();
                while (ip < code.Length && code[ip] != (byte)Op.ENDF) ip++;
                ip++;
                break;
            }
            case Op.CALL:
            {
                var fid = Pop();
                if ((uint)fid < (uint)_functions.Length && _functions[fid].Code is not null)
                {
                    var f = _functions[fid];
                    var body = new byte[f.Length];
                    Array.Copy(f.Code!, f.Start, body, 0, f.Length);
                    Execute(body);
                }
                break;
            }
            case Op.LOOPCALL:
            {
                var fid = Pop();
                var n = Pop();
                if ((uint)fid < (uint)_functions.Length && _functions[fid].Code is not null)
                {
                    var f = _functions[fid];
                    var body = new byte[f.Length];
                    Array.Copy(f.Code!, f.Start, body, 0, f.Length);
                    for (var i = 0; i < n; i++) Execute(body);
                }
                break;
            }
            case Op.ENDF:    /* return from CALL — handled by Execute returning */ break;

            // Hinting commands — left as TODO; pop expected args and continue.
            case Op.MDAP_min: case Op.MDAP_max:
            case Op.IUP_x:    case Op.IUP_y:
            case Op.ALIGNRP:
                _sp = 0; // hinting commands often expect specific stack states
                break;
            case Op.MIAP_min: case Op.MIAP_max:
                Pop(); Pop(); // cvtIdx, pointIdx
                break;

            // No-ops / silently consumed
            case Op.DEBUG: Pop(); break;
            case Op.AA: Pop(); break;
            case Op.SANGW: Pop(); break;

            default:
                // Unknown opcode — ignore. Safer than crashing the whole render.
                break;
        }
        return ip;
    }

    private void SetRound(int period, int phase, int threshold)
    {
        _gs.RoundPeriod = period;
        _gs.RoundPhase = phase;
        _gs.RoundThreshold = threshold;
    }

    private static int SkipToElseOrEif(byte[] code, int ip)
    {
        var depth = 1;
        while (ip < code.Length && depth > 0)
        {
            var op = code[ip++];
            ip = SkipOperands(code, ip, op);
            if (op == (byte)Op.IF) depth++;
            else if (op == (byte)Op.EIF) depth--;
            else if (op == (byte)Op.ELSE && depth == 1) return ip;
        }
        return ip;
    }

    private static int SkipToEif(byte[] code, int ip)
    {
        var depth = 1;
        while (ip < code.Length && depth > 0)
        {
            var op = code[ip++];
            ip = SkipOperands(code, ip, op);
            if (op == (byte)Op.IF) depth++;
            else if (op == (byte)Op.EIF) depth--;
        }
        return ip;
    }

    /// <summary>Skip the inline operands a packed-push opcode carries (so IF-skip walks correctly).</summary>
    private static int SkipOperands(byte[] code, int ip, byte op)
    {
        if (op is >= 0xB0 and <= 0xB7) return ip + (op & 7) + 1;
        if (op is >= 0xB8 and <= 0xBF) return ip + 2 * ((op & 7) + 1);
        if (op == (byte)Op.NPUSHB) { var n = code[ip]; return ip + 1 + n; }
        if (op == (byte)Op.NPUSHW) { var n = code[ip]; return ip + 1 + 2 * n; }
        return ip;
    }

    private void Push(int v)
    {
        if (_sp >= _stack.Length) return; // overflow — silently saturate (defensive)
        _stack[_sp++] = v;
    }

    private int Pop() => _sp > 0 ? _stack[--_sp] : 0;
    private int Peek() => _sp > 0 ? _stack[_sp - 1] : 0;

    private readonly record struct Function(byte[]? Code, int Start, int Length);
}
