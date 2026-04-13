namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// Per-opcode (pop, push) count table — high nibble = stack args consumed,
/// low nibble = items pushed. Lifted from FreeType's
/// <c>ttinterp.c</c> <c>Pop_Push_Count</c> table.
///
/// <para>Used by <see cref="Interpreter"/> to detect stack underflow before
/// dispatch: if the operand stack lacks enough args, FT's non-pedantic
/// behavior is to push zeros to fill the slot rather than throw — we do
/// the same.</para>
/// </summary>
internal static class PopPushCount
{
    private static byte P(int pop, int push) => (byte)((pop << 4) | push);

    public static int Pop(byte op) => Table[op] >> 4;
    public static int Push(byte op) => Table[op] & 0x0F;

    private static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        var t = new byte[256];

        // 0x00..0x0F
        t[0x00] = P(0, 0); // SVTCA[y]
        t[0x01] = P(0, 0); // SVTCA[x]
        t[0x02] = P(0, 0); // SPVTCA[y]
        t[0x03] = P(0, 0); // SPVTCA[x]
        t[0x04] = P(0, 0); // SFVTCA[y]
        t[0x05] = P(0, 0); // SFVTCA[x]
        t[0x06] = P(2, 0); // SPVTL[0]
        t[0x07] = P(2, 0); // SPVTL[1]
        t[0x08] = P(2, 0); // SFVTL[0]
        t[0x09] = P(2, 0); // SFVTL[1]
        t[0x0A] = P(2, 0); // SPVFS
        t[0x0B] = P(2, 0); // SFVFS
        t[0x0C] = P(0, 2); // GPV
        t[0x0D] = P(0, 2); // GFV
        t[0x0E] = P(0, 0); // SFVTPV
        t[0x0F] = P(5, 0); // ISECT

        // 0x10..0x1F
        t[0x10] = P(1, 0); // SRP0
        t[0x11] = P(1, 0); // SRP1
        t[0x12] = P(1, 0); // SRP2
        t[0x13] = P(1, 0); // SZP0
        t[0x14] = P(1, 0); // SZP1
        t[0x15] = P(1, 0); // SZP2
        t[0x16] = P(1, 0); // SZPS
        t[0x17] = P(1, 0); // SLOOP
        t[0x18] = P(0, 0); // RTG
        t[0x19] = P(0, 0); // RTHG
        t[0x1A] = P(1, 0); // SMD
        t[0x1B] = P(0, 0); // ELSE
        t[0x1C] = P(1, 0); // JMPR
        t[0x1D] = P(1, 0); // SCVTCI
        t[0x1E] = P(1, 0); // SSWCI
        t[0x1F] = P(1, 0); // SSW

        // 0x20..0x2F
        t[0x20] = P(1, 2); // DUP
        t[0x21] = P(1, 0); // POP
        t[0x22] = P(0, 0); // CLEAR
        t[0x23] = P(2, 2); // SWAP
        t[0x24] = P(0, 1); // DEPTH
        t[0x25] = P(1, 1); // CINDEX
        t[0x26] = P(1, 0); // MINDEX
        t[0x27] = P(2, 0); // ALIGNPTS
        t[0x28] = P(0, 0); // unused
        t[0x29] = P(1, 0); // UTP
        t[0x2A] = P(2, 0); // LOOPCALL
        t[0x2B] = P(1, 0); // CALL
        t[0x2C] = P(1, 0); // FDEF
        t[0x2D] = P(0, 0); // ENDF
        t[0x2E] = P(1, 0); // MDAP[0]
        t[0x2F] = P(1, 0); // MDAP[1]

        // 0x30..0x3F
        t[0x30] = P(0, 0); // IUP[0] / IUP_y
        t[0x31] = P(0, 0); // IUP[1] / IUP_x
        t[0x32] = P(0, 0); // SHP[0] (loops)
        t[0x33] = P(0, 0); // SHP[1] (loops)
        t[0x34] = P(1, 0); // SHC[0]
        t[0x35] = P(1, 0); // SHC[1]
        t[0x36] = P(1, 0); // SHZ[0]
        t[0x37] = P(1, 0); // SHZ[1]
        t[0x38] = P(1, 0); // SHPIX (loops)
        t[0x39] = P(0, 0); // IP (loops)
        t[0x3A] = P(2, 0); // MSIRP[0]
        t[0x3B] = P(2, 0); // MSIRP[1]
        t[0x3C] = P(0, 0); // ALIGNRP (loops)
        t[0x3D] = P(0, 0); // RTDG
        t[0x3E] = P(2, 0); // MIAP[0]
        t[0x3F] = P(2, 0); // MIAP[1]

        // 0x40..0x4F
        t[0x40] = P(0, 0); // NPUSHB (decoded inline)
        t[0x41] = P(0, 0); // NPUSHW (decoded inline)
        t[0x42] = P(2, 0); // WS
        t[0x43] = P(1, 1); // RS
        t[0x44] = P(2, 0); // WCVTP
        t[0x45] = P(1, 1); // RCVT
        t[0x46] = P(1, 1); // GC[0]
        t[0x47] = P(1, 1); // GC[1]
        t[0x48] = P(2, 0); // SCFS
        t[0x49] = P(2, 1); // MD[0]
        t[0x4A] = P(2, 1); // MD[1]
        t[0x4B] = P(0, 1); // MPPEM
        t[0x4C] = P(0, 1); // MPS
        t[0x4D] = P(0, 0); // FLIPON
        t[0x4E] = P(0, 0); // FLIPOFF
        t[0x4F] = P(1, 0); // DEBUG

        // 0x50..0x5F
        t[0x50] = P(2, 1); // LT
        t[0x51] = P(2, 1); // LTEQ
        t[0x52] = P(2, 1); // GT
        t[0x53] = P(2, 1); // GTEQ
        t[0x54] = P(2, 1); // EQ
        t[0x55] = P(2, 1); // NEQ
        t[0x56] = P(1, 1); // ODD
        t[0x57] = P(1, 1); // EVEN
        t[0x58] = P(1, 0); // IF
        t[0x59] = P(0, 0); // EIF
        t[0x5A] = P(2, 1); // AND
        t[0x5B] = P(2, 1); // OR
        t[0x5C] = P(1, 1); // NOT
        t[0x5D] = P(1, 0); // DELTAP1
        t[0x5E] = P(1, 0); // SDB
        t[0x5F] = P(1, 0); // SDS

        // 0x60..0x6F
        t[0x60] = P(2, 1); // ADD
        t[0x61] = P(2, 1); // SUB
        t[0x62] = P(2, 1); // DIV
        t[0x63] = P(2, 1); // MUL
        t[0x64] = P(1, 1); // ABS
        t[0x65] = P(1, 1); // NEG
        t[0x66] = P(1, 1); // FLOOR
        t[0x67] = P(1, 1); // CEILING
        for (var i = 0x68; i <= 0x6F; i++) t[i] = P(1, 1); // ROUND[0..3] / NROUND[0..3]

        // 0x70..0x7F
        t[0x70] = P(2, 0); // WCVTF
        t[0x71] = P(1, 0); // DELTAP2
        t[0x72] = P(1, 0); // DELTAP3
        t[0x73] = P(1, 0); // DELTAC1
        t[0x74] = P(1, 0); // DELTAC2
        t[0x75] = P(1, 0); // DELTAC3
        t[0x76] = P(1, 0); // SROUND
        t[0x77] = P(1, 0); // S45ROUND
        t[0x78] = P(2, 0); // JROT
        t[0x79] = P(2, 0); // JROF
        t[0x7A] = P(0, 0); // ROFF
        t[0x7B] = P(0, 0); // unused
        t[0x7C] = P(0, 0); // RUTG
        t[0x7D] = P(0, 0); // RDTG
        t[0x7E] = P(1, 0); // SANGW
        t[0x7F] = P(1, 0); // AA

        // 0x80..0x8F
        t[0x80] = P(0, 0); // FLIPPT (loops)
        t[0x81] = P(2, 0); // FLIPRGON
        t[0x82] = P(2, 0); // FLIPRGOFF
        t[0x83] = P(0, 0); // unused
        t[0x84] = P(0, 0); // unused
        t[0x85] = P(1, 0); // SCANCTRL
        t[0x86] = P(2, 0); // SDPVTL[0]
        t[0x87] = P(2, 0); // SDPVTL[1]
        t[0x88] = P(1, 1); // GETINFO
        t[0x89] = P(1, 0); // IDEF
        t[0x8A] = P(3, 3); // ROLL
        t[0x8B] = P(2, 1); // MAX
        t[0x8C] = P(2, 1); // MIN
        t[0x8D] = P(1, 0); // SCANTYPE
        t[0x8E] = P(2, 0); // INSTCTRL

        // 0x90..0xAF — mostly unused (some reserved for variable-font / SubPixel).
        t[0x91] = P(0, 0); // GETVAR (handled specially)
        t[0x92] = P(0, 1); // GETDATA

        // 0xB0..0xB7 PUSHB[abc] — operands consumed inline; pop = 0, push = abc + 1
        for (var i = 0xB0; i <= 0xB7; i++) t[i] = P(0, (i & 7) + 1);
        // 0xB8..0xBF PUSHW[abc] — same shape
        for (var i = 0xB8; i <= 0xBF; i++) t[i] = P(0, (i & 7) + 1);

        // 0xC0..0xDF MDRP[abcde] — pop 1
        for (var i = 0xC0; i <= 0xDF; i++) t[i] = P(1, 0);
        // 0xE0..0xFF MIRP[abcde] — pop 2
        for (var i = 0xE0; i <= 0xFF; i++) t[i] = P(2, 0);

        return t;
    }
}
