namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// Immutable snapshot of a TrueType interpreter after fpgm + prep have run.
/// Safe to share across threads. <see cref="HintingPipeline"/> caches one
/// per (font, ppem) and clones it into a per-call <see cref="Interpreter"/>
/// for each glyph render.
/// </summary>
internal sealed class HintingSnapshot
{
    public readonly Interpreter.Function[] Functions; // function table (immutable after fpgm)
    public readonly int[] Cvt;              // F26.6, scaled + possibly patched by prep
    public readonly int[] Storage;          // per-face storage area
    public readonly Zone Twilight;          // twilight zone (zone 0)
    public readonly GraphicsState Gs;       // graphics state after prep
    public readonly float Ppem;
    public readonly int Scale26_6;
    public readonly int StackSize;

    public HintingSnapshot(Interpreter.Function[] functions, int[] cvt, int[] storage,
        Zone twilight, GraphicsState gs, float ppem, int scale, int stackSize)
    {
        Functions = functions;
        Cvt = cvt;
        Storage = storage;
        Twilight = twilight;
        Gs = gs;
        Ppem = ppem;
        Scale26_6 = scale;
        StackSize = stackSize;
    }
}

/// <summary>
/// TrueType bytecode interpreter.
///
/// <para>Implements the dispatch loop, operand stack, function table,
/// graphics state, and the v40 opcodes needed for Phase 8 hinting.</para>
///
/// <para><b>Usage:</b> build once per face via the primary constructor + fpgm +
/// prep, then take a <see cref="HintingSnapshot"/>. For each glyph render,
/// create a per-call instance via <see cref="Interpreter(HintingSnapshot)"/>
/// which clones the mutable arrays. This keeps <c>OpenTypeFont</c> lock-free
/// and thread-safe.</para>
/// </summary>
internal sealed class Interpreter
{
    // ---- Stack -----------------------------------------------------------
    private readonly int[] _stack;
    private int _sp;

    // ---- Storage / CVT --------------------------------------------------
    private int[] _storage;                // per face, persists across runs
    private int[] _cvt;                    // F26.6, scaled to current ppem
    private ushort[]? _cvtFunits;          // raw FUnit values from 'cvt ' table (null in snapshot-cloned instances)

    // ---- Function table -------------------------------------------------
    private readonly Function[] _functions;

    // ---- Per-glyph state ------------------------------------------------
    private GraphicsState _gs;
    /// <summary>True while executing a glyph program (not fpgm/prep). In v40
    /// grayscale mode, X-direction point movements are suppressed in glyph
    /// programs to preserve sub-pixel positioning (matching FT behavior).</summary>
    private bool _inGlyphProgram;
    private Zone _twilight;
    // Default placeholder so prep programs that touch glyph-zone state during
    // size change don't NPE. Real zone is plumbed in by RunGlyphProgram.
    private Zone _glyph = new(0);

    // ---- Scaling --------------------------------------------------------
    private float _ppem;
    private int _scale_26_6; // pixels per font-unit, F26.6

    private const int MaxFunctionId = 256;

    /// <summary>Primary constructor — used to build the face-level interpreter
    /// that runs fpgm + prep to produce a <see cref="HintingSnapshot"/>.</summary>
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

    /// <summary>Per-call constructor — clones mutable state from a cached
    /// <see cref="HintingSnapshot"/> so each glyph render is independent.
    /// The function table is shared (read-only after fpgm).</summary>
    public Interpreter(HintingSnapshot snap)
    {
        _stack = new int[snap.StackSize];
        _storage = (int[])snap.Storage.Clone();
        _cvt = (int[])snap.Cvt.Clone();
        _functions = snap.Functions; // shared — read-only after fpgm
        _twilight = snap.Twilight.Clone();
        _gs = snap.Gs;
        _ppem = snap.Ppem;
        _scale_26_6 = snap.Scale26_6;
        _cvtFunits = null; // not needed for per-glyph execution
    }

    /// <summary>Capture the current interpreter state as an immutable snapshot
    /// (call after fpgm + prep have run).</summary>
    public HintingSnapshot TakeSnapshot() => new(
        _functions, (int[])_cvt.Clone(), (int[])_storage.Clone(),
        _twilight.Clone(), _gs, _ppem, _scale_26_6, _stack.Length);

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
        if (_cvtFunits is not null)
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
        _inGlyphProgram = true;
        Execute(instructions);
        _inGlyphProgram = false;
    }

    private int ScaleFunits(int funits)
    {
        // Round-to-nearest: truncating with `>> 6` rounds toward zero, which loses
        // a half-bit on every scaled FUnit and propagates into MIRP/MDAP rounding
        // (e.g. cap-height 1493 funits at 24 ppem comes out at 1119 vs. 1120,
        // which then rounds to 17 px instead of 18 px).
        var product = (long)funits * _scale_26_6;
        return (int)((product + (product >= 0 ? 32 : -32)) >> 6);
    }

    /// <summary>Convert a design-unit (FUnit) value to scaled F26.6 pixels using
    /// the current size set by <see cref="OnSizeChange"/>. Returns 0 if no size
    /// has been set yet.</summary>
    public int ScaleFunitsToPx(int funits) => ScaleFunits(funits);

    /// <summary>Current pixels-per-em; 0 until <see cref="OnSizeChange"/> runs.</summary>
    public float CurrentPpem => _ppem;

    // ---- Dispatch loop ---------------------------------------------------

    private void Execute(byte[] code)
    {
        // FreeType-style underflow handling (ttinterp.c §TT_RunIns):
        // before each opcode dispatches, if the stack lacks enough args for
        // the opcode's documented pop count, fill the missing slots with
        // zeros instead of throwing. This is FT's non-pedantic default.
        // Per-instruction range checks (e.g. MINDEX with k > sp) silently
        // no-op — also matches FT.
        var ip = 0;
        while (ip < code.Length)
        {
            var op = code[ip++];
            var pop = PopPushCount.Pop(op);
            if (pop > _sp)
            {
                var deficit = pop - _sp;
                for (var i = 0; i < deficit && _sp < _stack.Length; i++)
                    _stack[_sp++] = 0;
            }
            ip = Dispatch(op, code, ip);
        }
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
        if (op is >= 0xC0 and <= 0xDF) { ExecMdrp(op); return ip; }
        if (op is >= 0xE0)             { ExecMirp(op); return ip; }

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

            // Storage / CVT — uint cast covers both negative and oversized in
            // one comparison.
            case Op.RS:     { var i = Pop(); Push((uint)i < (uint)_storage.Length ? _storage[i] : 0); break; }
            case Op.WS:     { var v = Pop(); var i = Pop(); if ((uint)i < (uint)_storage.Length) _storage[i] = v; break; }
            case Op.RCVT:   { var i = Pop(); Push((uint)i < (uint)_cvt.Length ? _cvt[i] : 0); break; }
            case Op.WCVTP:  { var v = Pop(); var i = Pop(); if ((uint)i < (uint)_cvt.Length) _cvt[i] = v; break; }
            case Op.WCVTF:  { var v = Pop(); var i = Pop(); if ((uint)i < (uint)_cvt.Length) _cvt[i] = ScaleFunits(v); break; }

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
            case Op.RTG:   _gs.RoundMode = RoundMode.Grid;       SetRound(64, 0, 32); break;
            case Op.RTHG:  _gs.RoundMode = RoundMode.HalfGrid;   SetRound(64, 32, 32); break;
            case Op.RTDG:  _gs.RoundMode = RoundMode.DoubleGrid; SetRound(32, 0, 16); break;
            case Op.RDTG:  _gs.RoundMode = RoundMode.DownToGrid; SetRound(64, 0, 63); break;
            case Op.RUTG:  _gs.RoundMode = RoundMode.UpToGrid;   SetRound(64, 0, 0); break;
            case Op.ROFF:  _gs.RoundMode = RoundMode.Off;        break;
            case Op.SROUND:
            {
                var arg = (byte)Pop();
                Rounding.DecodeSRoundArg(arg, super45: false, out var per, out var ph, out var th);
                _gs.RoundMode = RoundMode.Super;
                SetRound(per, ph, th);
                break;
            }
            case Op.S45ROUND:
            {
                var arg = (byte)Pop();
                Rounding.DecodeSRoundArg(arg, super45: true, out var per, out var ph, out var th);
                _gs.RoundMode = RoundMode.Super45;
                SetRound(per, ph, th);
                break;
            }

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

            // Function defs — only allowed in fpgm/prep, not glyph programs.
            // The _functions array is shared across concurrent per-glyph interpreters
            // (thread-safe because it's read-only after fpgm). Reject FDEF/IDEF
            // during glyph execution to prevent writes to the shared array.
            case Op.FDEF:
            {
                var fid = Pop();
                var bodyStart = ip;
                while (ip < code.Length && code[ip] != (byte)Op.ENDF) ip++;
                if (!_inGlyphProgram && fid >= 0 && fid < _functions.Length)
                    _functions[fid] = new Function(code, bodyStart, ip - bodyStart);
                ip++; // skip ENDF
                break;
            }
            case Op.IDEF:
            {
                // Instruction definition — overrides an unused opcode. Rare;
                // skip body during glyph programs (same reason as FDEF).
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

            // Hinting commands.
            case Op.MDAP_min: ExecMdap(round: false); break;
            case Op.MDAP_max: ExecMdap(round: true);  break;
            case Op.MIAP_min: ExecMiap(round: false); break;
            case Op.MIAP_max: ExecMiap(round: true);  break;
            case Op.ALIGNRP: ExecAlignRp(); break;
            case Op.ALIGNPTS: ExecAlignPts(); break;
            case Op.UTP:
            {
                var p = Pop();
                var z = GetZone(_gs.Zp0);
                if ((uint)p < (uint)z.PointCount)
                {
                    byte mask = 0;
                    if (_gs.FreeX != 0) mask |= Zone.FlagTouchedX;
                    if (_gs.FreeY != 0) mask |= Zone.FlagTouchedY;
                    z.Flags[p] &= (byte)~mask;
                }
                break;
            }
            case Op.IUP_x: ExecIup(xAxis: true);  break;
            case Op.IUP_y: ExecIup(xAxis: false); break;
            case Op.IP:    ExecIp(); break;
            case Op.SHP_min: ExecShp(useRp2: true);  break; // uses zp0/rp1 in min variant per spec
            case Op.SHP_max: ExecShp(useRp2: false); break;
            case Op.SHC_min: ExecShc(useRp2: true);  break;
            case Op.SHC_max: ExecShc(useRp2: false); break;
            case Op.SHZ_min: ExecShz(useRp2: true);  break;
            case Op.SHZ_max: ExecShz(useRp2: false); break;
            case Op.SHPIX:   ExecShpix(); break;
            case Op.MSIRP_min: ExecMsirp(setRp0: false); break;
            case Op.MSIRP_max: ExecMsirp(setRp0: true);  break;

            // Query verbs
            case Op.GC_cur:  ExecGc(useOriginal: false); break;
            case Op.GC_orig: ExecGc(useOriginal: true);  break;
            case Op.SCFS:    ExecScfs(); break;
            case Op.MD_cur:  ExecMd(useOriginal: false); break;
            case Op.MD_orig: ExecMd(useOriginal: true);  break;

            // Vector setters
            case Op.SPVFS:
            {
                var y = (short)Pop();
                var x = (short)Pop();
                _gs.ProjX = x; _gs.ProjY = y;
                _gs.DualX = x; _gs.DualY = y;
                break;
            }
            case Op.SFVFS:
            {
                var y = (short)Pop();
                var x = (short)Pop();
                _gs.FreeX = x; _gs.FreeY = y;
                break;
            }
            case Op.SFVTPV:
                _gs.FreeX = _gs.ProjX; _gs.FreeY = _gs.ProjY;
                break;
            case Op.SPVTL_min: SetVectorFromLine(setProj: true,  perpendicular: false); break;
            case Op.SPVTL_max: SetVectorFromLine(setProj: true,  perpendicular: true);  break;
            case Op.SFVTL_min: SetVectorFromLine(setProj: false, perpendicular: false); break;
            case Op.SFVTL_max: SetVectorFromLine(setProj: false, perpendicular: true);  break;
            case Op.SDPVTL_min: SetDualVectorFromLine(perpendicular: false); break;
            case Op.SDPVTL_max: SetDualVectorFromLine(perpendicular: true);  break;

            // Flip on-curve flags
            case Op.FLIPPT:
            {
                var z = GetZone(1); // zone 1 only per spec
                for (var i = 0; i < _gs.Loop; i++)
                {
                    var p = Pop();
                    if ((uint)p < (uint)z.PointCount)
                        z.Flags[p] = (byte)(z.Flags[p] ^ Zone.FlagOnCurve);
                }
                _gs.Loop = 1;
                break;
            }
            case Op.FLIPRGON:
            case Op.FLIPRGOFF:
            {
                var hi = Pop();
                var lo = Pop();
                var z = GetZone(1);
                var on = ((Op)op) == Op.FLIPRGON;
                for (var p = lo; p <= hi && (uint)p < (uint)z.PointCount; p++)
                {
                    if (on) z.Flags[p] |= Zone.FlagOnCurve;
                    else z.Flags[p] &= unchecked((byte)~Zone.FlagOnCurve);
                }
                break;
            }

            // ISECT — move point p to the intersection of line(a0,a1) and
            // line(b0,b1). All points read from the glyph zone (zone 1).
            // Stack (top→bottom): a0, a1, b0, b1, p.
            case Op.ISECT:
            {
                var a0 = Pop();
                var a1 = Pop();
                var b0 = Pop();
                var b1 = Pop();
                var p  = Pop();
                var z = _glyph;
                if ((uint)a0 >= (uint)z.PointCount || (uint)a1 >= (uint)z.PointCount ||
                    (uint)b0 >= (uint)z.PointCount || (uint)b1 >= (uint)z.PointCount ||
                    (uint)p  >= (uint)z.PointCount) break;

                // Line A: from (ax0,ay0) to (ax1,ay1); Line B: from (bx0,by0) to (bx1,by1).
                // Use long arithmetic to avoid overflow with F26.6 values.
                long ax0 = z.CurX[a0], ay0 = z.CurY[a0];
                long ax1 = z.CurX[a1], ay1 = z.CurY[a1];
                long bx0 = z.CurX[b0], by0 = z.CurY[b0];
                long bx1 = z.CurX[b1], by1 = z.CurY[b1];

                long dax = ax1 - ax0, day = ay1 - ay0;
                long dbx = bx1 - bx0, dby = by1 - by0;
                long denom = dax * dby - day * dbx;

                if (denom == 0)
                {
                    // Parallel lines — place at midpoint of the four endpoints (FT behavior).
                    z.CurX[p] = (int)((ax0 + ax1 + bx0 + bx1) / 4);
                    z.CurY[p] = (int)((ay0 + ay1 + by0 + by1) / 4);
                }
                else
                {
                    // Cramer's rule: t = ((bx0-ax0)*dby - (by0-ay0)*dbx) / denom
                    long num = (bx0 - ax0) * dby - (by0 - ay0) * dbx;
                    z.CurX[p] = (int)(ax0 + (num * dax) / denom);
                    z.CurY[p] = (int)(ay0 + (num * day) / denom);
                }
                z.Flags[p] |= Zone.FlagTouchedX | Zone.FlagTouchedY;
                break;
            }

            case Op.INSTCTRL:
            {
                var selector = Pop();
                var value = Pop();
                _gs.InstructControl = (byte)((selector & 0xFF) | ((value & 0xFF) << 8));
                break;
            }

            // DELTAP1/2/3 — per-ppem point exception lists.
            // Stack (top→bottom): n, (p_n, arg_n), …, (p_1, arg_1)
            // arg byte: high nibble = relative ppem, low nibble = magnitude code.
            // Exception fires when round(current_ppem) == delta_base + bias + relPpem,
            // where bias = 0/16/32 for DELTAP1/2/3. Magnitude code 0..15 decodes to
            // {-8..-1, 1..8} steps; each step = 1 / (2^delta_shift) pixels.
            // Result is applied as a sub-pixel shift along the freedom vector to
            // points in zp0 (per TT spec).
            case Op.DELTAP1: ExecDeltap(bias: 0);  break;
            case Op.DELTAP2: ExecDeltap(bias: 16); break;
            case Op.DELTAP3: ExecDeltap(bias: 32); break;

            // DELTAC1/2/3 — same exception encoding but patches CVT[p] rather
            // than moving a glyph point. Useful for tuning stem widths at
            // specific ppem.
            case Op.DELTAC1: ExecDeltac(bias: 0);  break;
            case Op.DELTAC2: ExecDeltac(bias: 16); break;
            case Op.DELTAC3: ExecDeltac(bias: 32); break;

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

    internal readonly record struct Function(byte[]? Code, int Start, int Length);

    // ---- Zone access ----------------------------------------------------

    private Zone GetZone(byte zp) => zp == 0 ? _twilight : _glyph;

    // ---- Vector projection / movement -----------------------------------
    //
    // Vectors (proj/free/dual) are stored as F2.14 (1.0 = 0x4000). Point
    // coordinates are F26.6. A "distance along v" = (dx, dy) · v in F26.6.
    // We use long-arithmetic intermediates to avoid overflow.

    private int Project(int dx, int dy)
        => (int)(((long)dx * _gs.ProjX + (long)dy * _gs.ProjY + 0x2000) >> 14);

    private int DualProject(int dx, int dy)
        => (int)(((long)dx * _gs.DualX + (long)dy * _gs.DualY + 0x2000) >> 14);

    /// <summary>F2.14 dot of the projection and freedom vectors.</summary>
    private int ProjFreeDot()
        => (int)(((long)_gs.ProjX * _gs.FreeX + (long)_gs.ProjY * _gs.FreeY + 0x2000) >> 14);

    /// <summary>
    /// Apply <paramref name="distance"/> (F26.6) along the freedom vector to
    /// <paramref name="pIdx"/> in <paramref name="z"/>. Sets touched flags on
    /// the components the freedom vector affects.
    /// </summary>
    private void MovePoint(Zone z, int pIdx, int distance)
    {
        if ((uint)pIdx >= (uint)z.PointCount) return;
        var pdf = ProjFreeDot();
        if (pdf == 0) return; // free vector perpendicular to proj vector — degenerate

        var fx = _gs.FreeX;
        var fy = _gs.FreeY;

        // v40 X-direction skip: in glyph programs, suppress X-only movements.
        // FT's "minimal subpixel hinting" (backwards_compatibility mode) does
        // this to preserve sub-pixel stem positioning for grayscale AA. Y hints
        // run normally; IUP[x] still runs (it doesn't use MovePoint). The point
        // is NOT marked touched so IUP[x] will interpolate it from scaled coords.
        if (_inGlyphProgram && fx != 0 && fy == 0)
            return;

        if (fx != 0)
        {
            var dF = (int)(((long)distance * fx) / pdf);
            z.CurX[pIdx] += dF;
            z.Flags[pIdx] |= Zone.FlagTouchedX;
        }
        if (fy != 0)
        {
            var dF = (int)(((long)distance * fy) / pdf);
            z.CurY[pIdx] += dF;
            z.Flags[pIdx] |= Zone.FlagTouchedY;
        }
    }

    /// <summary>
    /// Add engine compensation to a (signed) F26.6 distance, matching FreeType:
    /// compensation is applied to the magnitude (so a positive comp pushes
    /// negative distances further negative). For zero compensation this is a
    /// no-op — the common path in v40 / grayscale.
    /// </summary>
    private static int ApplyCompensation(int distance, int compensation)
    {
        if (compensation == 0) return distance;
        return distance >= 0 ? distance + compensation : distance - compensation;
    }

    /// <summary>
    /// Decode a DELTAP/DELTAC argument byte into (relative_ppem, F26.6 step
    /// count). <paramref name="step"/> is the signed magnitude expressed in
    /// units of 1 / 2^delta_shift pixels — i.e. one of {-8..-1, 1..8} (zero
    /// is unrepresentable in the encoding and never fires).
    /// </summary>
    private static void DecodeDeltaArg(byte arg, out int relPpem, out int step)
    {
        relPpem = (arg >> 4) & 0x0F;
        var mag = arg & 0x0F;
        // 0..7 → -8..-1, 8..15 → +1..+8.
        step = mag >= 8 ? mag - 7 : mag - 8;
    }

    /// <summary>Apply DELTAP1/2/3 — see opcode-block comment for stack layout.</summary>
    private void ExecDeltap(int bias)
    {
        var n = Pop();
        if (n <= 0) return;
        var ppemNow = (int)MathF.Round(_ppem);
        var z = GetZone(_gs.Zp0);
        // Each exception consumes (point, arg). Iterate even if some entries
        // miss the current ppem; the stack still needs draining.
        for (var i = 0; i < n; i++)
        {
            if (_sp < 2) return; // malformed bytecode — bail rather than corrupt.
            var p = Pop();
            var arg = (byte)Pop();
            DecodeDeltaArg(arg, out var relPpem, out var step);
            var targetPpem = (int)_gs.DeltaBase + bias + relPpem;
            if (targetPpem != ppemNow) continue;
            // Convert step (in 1/2^delta_shift-pixel units) to F26.6.
            // F26.6: 1 pixel = 64. step * (64 >> delta_shift) keeps things
            // safe when delta_shift > 6 (clamp to 0).
            var shift = 6 - (int)_gs.DeltaShift;
            var distance = shift >= 0 ? step << shift : step >> -shift;
            MovePoint(z, p, distance);
        }
    }

    /// <summary>Apply DELTAC1/2/3 — same exception encoding, patches CVT.</summary>
    private void ExecDeltac(int bias)
    {
        var n = Pop();
        if (n <= 0) return;
        var ppemNow = (int)MathF.Round(_ppem);
        for (var i = 0; i < n; i++)
        {
            if (_sp < 2) return;
            var c = Pop();
            var arg = (byte)Pop();
            DecodeDeltaArg(arg, out var relPpem, out var step);
            var targetPpem = (int)_gs.DeltaBase + bias + relPpem;
            if (targetPpem != ppemNow) continue;
            var shift = 6 - (int)_gs.DeltaShift;
            var distance = shift >= 0 ? step << shift : step >> -shift;
            if ((uint)c < (uint)_cvt.Length) _cvt[c] += distance;
        }
    }

    /// <summary>
    /// Move (or just touch) <paramref name="pIdx"/> to its grid-rounded position
    /// along the projection vector. Used by MDAP.
    /// </summary>
    private void ExecMdap(bool round)
    {
        var p = Pop();
        var z = GetZone(_gs.Zp0);
        if ((uint)p >= (uint)z.PointCount) { _gs.Rp0 = _gs.Rp1 = p; return; }
        if (round)
        {
            var cur = Project(z.CurX[p], z.CurY[p]);
            var snapped = Rounding.Round(cur, _gs);
            MovePoint(z, p, snapped - cur);
        }
        else
        {
            // Just touch the point — flags only.
            if (_gs.FreeX != 0) z.Flags[p] |= Zone.FlagTouchedX;
            if (_gs.FreeY != 0) z.Flags[p] |= Zone.FlagTouchedY;
        }
        _gs.Rp0 = p;
        _gs.Rp1 = p;
    }

    /// <summary>
    /// Snap point to a CVT value. Used by MIAP.
    /// </summary>
    private void ExecMiap(bool round)
    {
        var cvtIdx = Pop();
        var p = Pop();
        var z = GetZone(_gs.Zp0);
        var cvtVal = ((uint)cvtIdx < (uint)_cvt.Length) ? _cvt[cvtIdx] : 0;
        if ((uint)p >= (uint)z.PointCount) { _gs.Rp0 = _gs.Rp1 = p; return; }

        if (_gs.Zp0 == 0) // twilight: write the point's org coord from CVT along proj vector
        {
            z.OrgX[p] = (int)(((long)cvtVal * _gs.ProjX + 0x2000) >> 14);
            z.OrgY[p] = (int)(((long)cvtVal * _gs.ProjY + 0x2000) >> 14);
            z.CurX[p] = z.OrgX[p];
            z.CurY[p] = z.OrgY[p];
        }

        var distance = cvtVal;
        var orgDist  = Project(z.CurX[p], z.CurY[p]);
        if (round)
        {
            if (Math.Abs(distance - orgDist) > _gs.ControlValueCutIn)
                distance = orgDist;
            distance = Rounding.Round(distance, _gs);
        }
        MovePoint(z, p, distance - orgDist);
        _gs.Rp0 = p;
        _gs.Rp1 = p;
    }

    /// <summary>MDRP[abcde] (0xC0..0xDF).</summary>
    private void ExecMdrp(byte op)
    {
        bool setRp0    = (op & 0x10) != 0;
        bool useMin    = (op & 0x08) != 0;
        bool useRound  = (op & 0x04) != 0;
        int color      = op & 0x03; // 0=black, 1=white, 2=gray, 3=reserved
        var compensation = _gs.CompensationFor(color);

        var p = Pop();
        var zp1 = GetZone(_gs.Zp1);
        var zp0 = GetZone(_gs.Zp0);
        if ((uint)p >= (uint)zp1.PointCount || (uint)_gs.Rp0 >= (uint)zp0.PointCount)
        { if (setRp0) _gs.Rp0 = p; _gs.Rp1 = _gs.Rp0; _gs.Rp2 = p; return; }

        var orgDist = DualProject(zp1.OrgX[p] - zp0.OrgX[_gs.Rp0],
                                  zp1.OrgY[p] - zp0.OrgY[_gs.Rp0]);

        // Single-width cut-in
        if (Math.Abs(orgDist - _gs.SingleWidthValue) < _gs.SingleWidthCutIn)
            orgDist = orgDist >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;

        // Engine compensation is added before rounding (FT ttinterp.c: in
        // Round_*(), the `compensation` argument is added to |distance|, then
        // the magnitude is rounded, then the sign is restored). Reproduce that
        // sign-aware behavior here so positive comp pushes both positive and
        // negative distances away from zero.
        var distance = useRound
            ? Rounding.Round(ApplyCompensation(orgDist, compensation), _gs)
            : ApplyCompensation(orgDist, compensation);

        if (useMin)
        {
            if (orgDist >= 0) { if (distance < _gs.MinimumDistance) distance = _gs.MinimumDistance; }
            else              { if (distance > -_gs.MinimumDistance) distance = -_gs.MinimumDistance; }
        }

        var curDist = Project(zp1.CurX[p] - zp0.CurX[_gs.Rp0],
                              zp1.CurY[p] - zp0.CurY[_gs.Rp0]);
        MovePoint(zp1, p, distance - curDist);

        _gs.Rp1 = _gs.Rp0;
        _gs.Rp2 = p;
        if (setRp0) _gs.Rp0 = p;
    }

    /// <summary>MIRP[abcde] (0xE0..0xFF).</summary>
    private void ExecMirp(byte op)
    {
        bool setRp0   = (op & 0x10) != 0;
        bool useMin   = (op & 0x08) != 0;
        bool useRound = (op & 0x04) != 0;
        int  color    = op & 0x03;
        var  compensation = _gs.CompensationFor(color);

        var cvtIdx = Pop();
        var p = Pop();
        var zp1 = GetZone(_gs.Zp1);
        var zp0 = GetZone(_gs.Zp0);
        var cvtVal = ((uint)cvtIdx < (uint)_cvt.Length) ? _cvt[cvtIdx] : 0;

        if ((uint)p >= (uint)zp1.PointCount || (uint)_gs.Rp0 >= (uint)zp0.PointCount)
        { if (setRp0) _gs.Rp0 = p; _gs.Rp1 = _gs.Rp0; _gs.Rp2 = p; return; }

        // Single-width cut-in on the CVT value.
        if (Math.Abs(cvtVal - _gs.SingleWidthValue) < _gs.SingleWidthCutIn)
            cvtVal = cvtVal >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;

        if (_gs.Zp1 == 0) // twilight: project rp0 + cvtVal*proj as the point's org/cur
        {
            zp1.OrgX[p] = zp0.OrgX[_gs.Rp0] + (int)(((long)cvtVal * _gs.ProjX + 0x2000) >> 14);
            zp1.OrgY[p] = zp0.OrgY[_gs.Rp0] + (int)(((long)cvtVal * _gs.ProjY + 0x2000) >> 14);
            zp1.CurX[p] = zp1.OrgX[p];
            zp1.CurY[p] = zp1.OrgY[p];
        }

        var orgDist = DualProject(zp1.OrgX[p] - zp0.OrgX[_gs.Rp0],
                                  zp1.OrgY[p] - zp0.OrgY[_gs.Rp0]);
        var curDist = Project(zp1.CurX[p] - zp0.CurX[_gs.Rp0],
                              zp1.CurY[p] - zp0.CurY[_gs.Rp0]);

        // AutoFlip: if signs disagree, flip the CVT value.
        if (_gs.AutoFlip && (orgDist ^ cvtVal) < 0) cvtVal = -cvtVal;

        // Cut-in: if the original distance is too far from the CVT value, use the
        // original (unrounded) instead.
        var distance = cvtVal;
        if (_gs.Zp1 != 0 && _gs.Zp0 != 0
            && Math.Abs(cvtVal - orgDist) > _gs.ControlValueCutIn)
            distance = orgDist;

        // Engine compensation applied pre-rounding (see ExecMdrp comment).
        distance = ApplyCompensation(distance, compensation);
        if (useRound) distance = Rounding.Round(distance, _gs);

        if (useMin)
        {
            if (orgDist >= 0) { if (distance < _gs.MinimumDistance) distance = _gs.MinimumDistance; }
            else              { if (distance > -_gs.MinimumDistance) distance = -_gs.MinimumDistance; }
        }

        MovePoint(zp1, p, distance - curDist);

        _gs.Rp1 = _gs.Rp0;
        _gs.Rp2 = p;
        if (setRp0) _gs.Rp0 = p;
    }

    /// <summary>ALIGNRP — move each looped point to lie on rp0 along projection.</summary>
    private void ExecAlignRp()
    {
        var loop = Math.Max(1, _gs.Loop);
        var zp0 = GetZone(_gs.Zp0);
        var zp1 = GetZone(_gs.Zp1);
        if ((uint)_gs.Rp0 >= (uint)zp0.PointCount) { _gs.Loop = 1; return; }
        for (var i = 0; i < loop; i++)
        {
            var p = Pop();
            if ((uint)p >= (uint)zp1.PointCount) continue;
            var dist = Project(zp1.CurX[p] - zp0.CurX[_gs.Rp0],
                               zp1.CurY[p] - zp0.CurY[_gs.Rp0]);
            MovePoint(zp1, p, -dist);
        }
        _gs.Loop = 1;
    }

    /// <summary>ALIGNPTS — align two points to their midpoint along projection.</summary>
    private void ExecAlignPts()
    {
        var p1 = Pop();
        var p2 = Pop();
        var z1 = GetZone(_gs.Zp1);
        var z2 = GetZone(_gs.Zp0);
        if ((uint)p1 >= (uint)z1.PointCount || (uint)p2 >= (uint)z2.PointCount) return;
        var dist = Project(z2.CurX[p2] - z1.CurX[p1],
                           z2.CurY[p2] - z1.CurY[p1]) / 2;
        MovePoint(z1, p1, dist);
        MovePoint(z2, p2, -dist);
    }

    /// <summary>IP — interpolate looped points between rp1 (in zp0) and rp2 (in zp1).</summary>
    private void ExecIp()
    {
        var loop = Math.Max(1, _gs.Loop);
        var zp0 = GetZone(_gs.Zp0);
        var zp1 = GetZone(_gs.Zp1);
        var zp2 = GetZone(_gs.Zp2);
        if ((uint)_gs.Rp1 >= (uint)zp0.PointCount || (uint)_gs.Rp2 >= (uint)zp1.PointCount)
        {
            for (var i = 0; i < loop; i++) Pop();
            _gs.Loop = 1; return;
        }

        var orgRp1 = DualProject(zp0.OrgX[_gs.Rp1], zp0.OrgY[_gs.Rp1]);
        var orgRp2 = DualProject(zp1.OrgX[_gs.Rp2], zp1.OrgY[_gs.Rp2]);
        var curRp1 = Project(zp0.CurX[_gs.Rp1], zp0.CurY[_gs.Rp1]);
        var curRp2 = Project(zp1.CurX[_gs.Rp2], zp1.CurY[_gs.Rp2]);

        var orgRange = orgRp2 - orgRp1;
        var curRange = curRp2 - curRp1;

        for (var i = 0; i < loop; i++)
        {
            var p = Pop();
            if ((uint)p >= (uint)zp2.PointCount) continue;
            var orgP = DualProject(zp2.OrgX[p], zp2.OrgY[p]);
            var curP = Project(zp2.CurX[p], zp2.CurY[p]);

            int newP;
            if (orgRange == 0)
            {
                // Anchors had identical original projection: shift by curRp1's delta.
                newP = curRp1 + (orgP - orgRp1);
            }
            else
            {
                // Linear interpolation in original space → mapped to current space.
                newP = curRp1 + (int)(((long)(orgP - orgRp1) * curRange) / orgRange);
            }
            MovePoint(zp2, p, newP - curP);
        }
        _gs.Loop = 1;
    }

    /// <summary>SHP — shift looped points by the same delta that moved rp1 (zp0) or rp2 (zp1).</summary>
    private void ExecShp(bool useRp2)
    {
        // Spec (OpenType): SHP[a]
        //   a = 0 (opcode 0x32): anchor = rp2 in zone pointed to by zp1
        //   a = 1 (opcode 0x33): anchor = rp1 in zone pointed to by zp0
        // Dispatch passes useRp2=true for opcode 0x32 (a=0). The previous body
        // had the branches swapped, which made SHP shift looped points by the
        // wrong reference's delta — visible on glyphs like 'H' where SHP after
        // a Y-axis MIRP is what propagates the cap-line snap to the other top
        // contour points.
        var (anchorIdx, anchorZone) = useRp2
            ? (_gs.Rp2, GetZone(_gs.Zp1))
            : (_gs.Rp1, GetZone(_gs.Zp0));
        var loop = Math.Max(1, _gs.Loop);
        var z = GetZone(_gs.Zp2);
        if ((uint)anchorIdx >= (uint)anchorZone.PointCount)
        {
            for (var i = 0; i < loop; i++) Pop();
            _gs.Loop = 1; return;
        }
        var dist = Project(anchorZone.CurX[anchorIdx] - anchorZone.OrgX[anchorIdx],
                           anchorZone.CurY[anchorIdx] - anchorZone.OrgY[anchorIdx]);
        for (var i = 0; i < loop; i++)
        {
            var p = Pop();
            if ((uint)p >= (uint)z.PointCount) continue;
            MovePoint(z, p, dist);
        }
        _gs.Loop = 1;
    }

    /// <summary>SHC — shift entire contour by the anchor's projection-delta.
    /// Same a-bit semantics as SHP (see <see cref="ExecShp"/>).</summary>
    private void ExecShc(bool useRp2)
    {
        var contourIdx = Pop();
        var (anchorIdx, anchorZone) = useRp2
            ? (_gs.Rp2, GetZone(_gs.Zp1))
            : (_gs.Rp1, GetZone(_gs.Zp0));
        if ((uint)anchorIdx >= (uint)anchorZone.PointCount) return;
        var dist = Project(anchorZone.CurX[anchorIdx] - anchorZone.OrgX[anchorIdx],
                           anchorZone.CurY[anchorIdx] - anchorZone.OrgY[anchorIdx]);

        // Contour ranges only known for the glyph zone (zone 1).
        if (_gs.Zp2 != 1 || _iupEnds is null) return;
        if ((uint)contourIdx >= (uint)_iupEnds.Length) return;

        var start = contourIdx == 0 ? 0 : _iupEnds[contourIdx - 1] + 1;
        var end = _iupEnds[contourIdx];
        var z = GetZone(1);
        for (var p = start; p <= end && p < z.PointCount; p++)
        {
            // Don't re-shift the anchor itself if it lies on this contour.
            if (anchorZone == z && p == anchorIdx) continue;
            MovePoint(z, p, dist);
        }
    }

    /// <summary>SHZ — shift every point in a zone by the anchor's projection-delta.
    /// Same a-bit semantics as SHP (see <see cref="ExecShp"/>).</summary>
    private void ExecShz(bool useRp2)
    {
        var zoneId = Pop();
        var (anchorIdx, anchorZone) = useRp2
            ? (_gs.Rp2, GetZone(_gs.Zp1))
            : (_gs.Rp1, GetZone(_gs.Zp0));
        var z = GetZone((byte)zoneId);
        if ((uint)anchorIdx >= (uint)anchorZone.PointCount) return;
        var dist = Project(anchorZone.CurX[anchorIdx] - anchorZone.OrgX[anchorIdx],
                           anchorZone.CurY[anchorIdx] - anchorZone.OrgY[anchorIdx]);
        // SHZ shifts ALL points but doesn't touch them (per spec). For phantom
        // points at the end of zone 1 this matters; we still apply the shift.
        // Skip the 4 phantom-point indices' "touched" effect — MovePoint sets
        // touched flags. For this Phase 8 cut, cumulative effect is acceptable.
        for (var p = 0; p < z.PointCount; p++) MovePoint(z, p, dist);
    }

    /// <summary>SHPIX — shift looped points by a stack-supplied pixel amount along free vector.</summary>
    private void ExecShpix()
    {
        var amount = Pop();
        var loop = Math.Max(1, _gs.Loop);
        var z = GetZone(_gs.Zp2);
        for (var i = 0; i < loop; i++)
        {
            var p = Pop();
            if ((uint)p >= (uint)z.PointCount) continue;
            // SHPIX moves directly along the freedom vector (no projection).
            if (_gs.FreeX != 0)
            {
                var dx = (int)(((long)amount * _gs.FreeX + 0x2000) >> 14);
                z.CurX[p] += dx;
                z.Flags[p] |= Zone.FlagTouchedX;
            }
            if (_gs.FreeY != 0)
            {
                var dy = (int)(((long)amount * _gs.FreeY + 0x2000) >> 14);
                z.CurY[p] += dy;
                z.Flags[p] |= Zone.FlagTouchedY;
            }
        }
        _gs.Loop = 1;
    }

    /// <summary>MSIRP — move stack indirect relative point.</summary>
    private void ExecMsirp(bool setRp0)
    {
        var distance = Pop();
        var p = Pop();
        var zp1 = GetZone(_gs.Zp1);
        var zp0 = GetZone(_gs.Zp0);
        if ((uint)p >= (uint)zp1.PointCount || (uint)_gs.Rp0 >= (uint)zp0.PointCount)
        { if (setRp0) _gs.Rp0 = p; _gs.Rp1 = _gs.Rp0; _gs.Rp2 = p; return; }

        if (_gs.Zp1 == 0)
        {
            zp1.OrgX[p] = zp0.OrgX[_gs.Rp0] + (int)(((long)distance * _gs.ProjX + 0x2000) >> 14);
            zp1.OrgY[p] = zp0.OrgY[_gs.Rp0] + (int)(((long)distance * _gs.ProjY + 0x2000) >> 14);
            zp1.CurX[p] = zp1.OrgX[p];
            zp1.CurY[p] = zp1.OrgY[p];
        }

        var curDist = Project(zp1.CurX[p] - zp0.CurX[_gs.Rp0],
                              zp1.CurY[p] - zp0.CurY[_gs.Rp0]);
        MovePoint(zp1, p, distance - curDist);

        _gs.Rp1 = _gs.Rp0;
        _gs.Rp2 = p;
        if (setRp0) _gs.Rp0 = p;
    }

    /// <summary>GC — get coordinate of point along projection vector.</summary>
    private void ExecGc(bool useOriginal)
    {
        var p = Pop();
        var z = GetZone(_gs.Zp2);
        if ((uint)p >= (uint)z.PointCount) { Push(0); return; }
        var v = useOriginal
            ? DualProject(z.OrgX[p], z.OrgY[p])
            : Project(z.CurX[p], z.CurY[p]);
        Push(v);
    }

    /// <summary>SCFS — set coordinate from stack along projection (move along free).</summary>
    private void ExecScfs()
    {
        var distance = Pop();
        var p = Pop();
        var z = GetZone(_gs.Zp2);
        if ((uint)p >= (uint)z.PointCount) return;
        var cur = Project(z.CurX[p], z.CurY[p]);
        MovePoint(z, p, distance - cur);
        if (_gs.Zp2 == 0)
        {
            z.OrgX[p] = z.CurX[p];
            z.OrgY[p] = z.CurY[p];
        }
    }

    /// <summary>MD — measure distance between two points along projection.</summary>
    private void ExecMd(bool useOriginal)
    {
        var p1 = Pop();
        var p2 = Pop();
        var z1 = GetZone(_gs.Zp1); // p1
        var z0 = GetZone(_gs.Zp0); // p2
        if ((uint)p1 >= (uint)z1.PointCount || (uint)p2 >= (uint)z0.PointCount)
        { Push(0); return; }
        int v;
        if (useOriginal)
            v = DualProject(z0.OrgX[p2] - z1.OrgX[p1], z0.OrgY[p2] - z1.OrgY[p1]);
        else
            v = Project(z0.CurX[p2] - z1.CurX[p1], z0.CurY[p2] - z1.CurY[p1]);
        Push(v);
    }

    /// <summary>SPVTL[a] / SFVTL[a] — set proj/free vector to a line through two points.</summary>
    private void SetVectorFromLine(bool setProj, bool perpendicular)
    {
        var p1 = Pop();
        var p2 = Pop();
        var z1 = GetZone(_gs.Zp1);
        var z2 = GetZone(_gs.Zp2);
        if ((uint)p1 >= (uint)z1.PointCount || (uint)p2 >= (uint)z2.PointCount) return;
        long dx = z2.CurX[p2] - z1.CurX[p1];
        long dy = z2.CurY[p2] - z1.CurY[p1];
        if (perpendicular) (dx, dy) = (-dy, dx);
        var (vx, vy) = NormalizeF214(dx, dy);
        if (setProj)
        {
            _gs.ProjX = vx; _gs.ProjY = vy;
            _gs.DualX = vx; _gs.DualY = vy;
        }
        else { _gs.FreeX = vx; _gs.FreeY = vy; }
    }

    /// <summary>SDPVTL[a] — set dual and projection vectors from a line in original coords.</summary>
    private void SetDualVectorFromLine(bool perpendicular)
    {
        var p1 = Pop();
        var p2 = Pop();
        var z1 = GetZone(_gs.Zp1);
        var z2 = GetZone(_gs.Zp2);
        if ((uint)p1 >= (uint)z1.PointCount || (uint)p2 >= (uint)z2.PointCount) return;
        long odx = z2.OrgX[p2] - z1.OrgX[p1];
        long ody = z2.OrgY[p2] - z1.OrgY[p1];
        long cdx = z2.CurX[p2] - z1.CurX[p1];
        long cdy = z2.CurY[p2] - z1.CurY[p1];
        if (perpendicular) { (odx, ody) = (-ody, odx); (cdx, cdy) = (-cdy, cdx); }
        var (dx, dy) = NormalizeF214(odx, ody);
        var (px, py) = NormalizeF214(cdx, cdy);
        _gs.DualX = dx; _gs.DualY = dy;
        _gs.ProjX = px; _gs.ProjY = py;
    }

    /// <summary>Normalize a (dx, dy) vector to F2.14 unit length.</summary>
    private static (short X, short Y) NormalizeF214(long dx, long dy)
    {
        if (dx == 0 && dy == 0) return (0x4000, 0); // arbitrary fallback
        var len = Math.Sqrt((double)dx * dx + (double)dy * dy);
        var x = (short)Math.Clamp((int)Math.Round(dx / len * 16384.0), short.MinValue, short.MaxValue);
        var y = (short)Math.Clamp((int)Math.Round(dy / len * 16384.0), short.MinValue, short.MaxValue);
        return (x, y);
    }

    // ---- IUP ------------------------------------------------------------

    /// <summary>
    /// IUP[xy] (0x30/0x31 — note: 0x30 is IUP_y, 0x31 is IUP_x per the spec).
    /// Interpolate untouched points in each contour using the touched ones as
    /// anchors. Operates on zone 1 only.
    /// </summary>
    private void ExecIup(bool xAxis)
    {
        // Need contour ranges. Phase 8 first cut: derive from glyph zone's
        // contour-end metadata, which Zone doesn't currently carry. Fall back
        // to no-op if absent — IUP becomes a no-op for now and points stay at
        // touched positions only. Wire-through provides the contour ends in
        // the next iteration of HintingPipeline.
        if (_iupEnds is null) return;

        var z = _glyph;
        if (z.PointCount == 0) return;
        // Iterate visible (non-phantom) points only.
        var n = _iupEnds.Length;
        var contourStart = 0;
        for (var c = 0; c < n; c++)
        {
            var contourEnd = _iupEnds[c];
            if (contourEnd >= z.PointCount) contourEnd = z.PointCount - 1;
            if (contourEnd < contourStart) { contourStart = contourEnd + 1; continue; }
            InterpolateContour(z, contourStart, contourEnd, xAxis);
            contourStart = contourEnd + 1;
        }
    }

    private static void InterpolateContour(Zone z, int start, int end, bool xAxis)
    {
        var len = end - start + 1;
        if (len <= 0) return;
        var touchMask = xAxis ? Zone.FlagTouchedX : Zone.FlagTouchedY;

        // First, find any touched point.
        int firstTouched = -1;
        for (var i = start; i <= end; i++)
            if ((z.Flags[i] & touchMask) != 0) { firstTouched = i; break; }
        if (firstTouched < 0) return; // nothing touched → no interpolation

        // Walk around the contour starting at firstTouched, processing runs
        // of untouched points between consecutive touched anchors. The loop
        // wraps back through firstTouched at step == len-1, which closes the
        // final untouched run between the last "real" anchor and firstTouched.
        var prevTouched = firstTouched;
        var sawOtherTouched = false;
        for (var step = 0; step < len; step++)
        {
            var i2 = start + ((firstTouched - start + step + 1) % len);
            if ((z.Flags[i2] & touchMask) != 0)
            {
                if (prevTouched != i2)
                    InterpolateRange(z, prevTouched, i2, start, end, xAxis);
                prevTouched = i2;
                if (i2 != firstTouched) sawOtherTouched = true;
            }
        }
        // Special case: only firstTouched is touched. Per FT, every untouched
        // point in the contour shifts by firstTouched's projection-delta.
        // (Don't run this when other touched points existed — the loop above
        // already interpolated everything; re-shifting would double-apply the
        // delta, which is what produced the uniform −31/64 px Y offset on H.)
        if (!sawOtherTouched)
        {
            int delta = xAxis
                ? z.CurX[firstTouched] - z.OrgX[firstTouched]
                : z.CurY[firstTouched] - z.OrgY[firstTouched];
            for (var i = start; i <= end; i++)
            {
                if (i == firstTouched) continue;
                if (xAxis) z.CurX[i] += delta;
                else       z.CurY[i] += delta;
            }
        }
    }

    /// <summary>
    /// Interpolate points strictly between <paramref name="a"/> and <paramref name="b"/>
    /// (anchors in contour [start, end]; wrap-around supported). For each
    /// untouched point p inside (a, b), in original space, p sits at some
    /// fraction t = (orig[p] - orig[a]) / (orig[b] - orig[a]); in current
    /// space, p maps to cur[a] + t * (cur[b] - cur[a]). FreeType's edge case:
    /// if orig[p] is outside [orig[a], orig[b]] (clamping), p is just shifted
    /// by the nearer anchor's delta.
    /// </summary>
    private static void InterpolateRange(Zone z, int a, int b, int start, int end, bool xAxis)
    {
        var len = end - start + 1;
        // Walk from (a + 1) to (b - 1) modulo (start, end).
        int orgA = xAxis ? z.OrgX[a] : z.OrgY[a];
        int orgB = xAxis ? z.OrgX[b] : z.OrgY[b];
        int curA = xAxis ? z.CurX[a] : z.CurY[a];
        int curB = xAxis ? z.CurX[b] : z.CurY[b];

        // Order anchors by original coord — interpolation works in sorted space.
        int orgLow, orgHigh, curLow, curHigh;
        if (orgA <= orgB) { orgLow = orgA; orgHigh = orgB; curLow = curA; curHigh = curB; }
        else              { orgLow = orgB; orgHigh = orgA; curLow = curB; curHigh = curA; }

        var span = orgHigh - orgLow;
        var deltaLow = curLow - orgLow;
        var deltaHigh = curHigh - orgHigh;

        var i = start + ((a - start + 1) % len);
        while (i != b)
        {
            int orgP = xAxis ? z.OrgX[i] : z.OrgY[i];
            int newP;
            if (orgP <= orgLow)       newP = orgP + deltaLow;
            else if (orgP >= orgHigh) newP = orgP + deltaHigh;
            else
            {
                // Linear interp.
                if (span == 0) newP = orgP + deltaLow;
                else
                {
                    var frac = (long)(orgP - orgLow);
                    newP = curLow + (int)((frac * (curHigh - curLow)) / span);
                }
            }
            if (xAxis) z.CurX[i] = newP;
            else       z.CurY[i] = newP;

            i = start + ((i - start + 1) % len);
        }
    }

    /// <summary>
    /// Contour end indices for the current glyph. Set by <see cref="HintingPipeline"/>
    /// before <see cref="RunGlyphProgram"/>; null when no glyph context (fpgm/prep).
    /// IUP needs this to know contour ranges.
    /// </summary>
    private int[]? _iupEnds;

    /// <summary>Set by <see cref="HintingPipeline"/> just before RunGlyphProgram.</summary>
    public void SetGlyphContours(int[]? contourEnds) => _iupEnds = contourEnds;
}
