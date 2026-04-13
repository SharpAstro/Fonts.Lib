using System.Collections.Frozen;

namespace SharpAstro.Fonts.Tables.Cff;

/// <summary>
/// A parsed CFF DICT. Key = operator code (1-byte ops are 0-31, 2-byte ops
/// are 0x0c00 + extended-op). Value = list of operands collected immediately
/// before the operator.
///
/// <para>Operands are decoded as <see cref="double"/> (covers integers and
/// 16.16 fixed). Arrays-of-numbers operands stay as multiple values under
/// the same key.</para>
///
/// <para>Spec: Adobe Tech Note #5176 §4 (operands) and §9 (operator table).</para>
/// </summary>
internal sealed class CffDict
{
    public FrozenDictionary<int, double[]> Entries { get; }

    private CffDict(FrozenDictionary<int, double[]> entries) => Entries = entries;

    public bool TryGetSingle(int op, out double value)
    {
        if (Entries.TryGetValue(op, out var arr) && arr.Length > 0)
        {
            value = arr[0];
            return true;
        }
        value = 0;
        return false;
    }

    public double GetSingleOr(int op, double fallback)
        => TryGetSingle(op, out var v) ? v : fallback;

    public bool TryGetArray(int op, out double[] arr)
    {
        if (Entries.TryGetValue(op, out var found))
        {
            arr = found;
            return true;
        }
        arr = [];
        return false;
    }

    /// <summary>Parse a DICT from <paramref name="data"/>.</summary>
    public static CffDict Parse(ReadOnlySpan<byte> data)
    {
        var entries = new Dictionary<int, double[]>();
        var operands = new List<double>(8);
        var i = 0;
        while (i < data.Length)
        {
            var b0 = data[i];
            if (b0 <= 21)
            {
                // Operator. 12 = escape; 2-byte op.
                int op;
                if (b0 == 12)
                {
                    op = 0x0c00 | data[i + 1];
                    i += 2;
                }
                else
                {
                    op = b0;
                    i++;
                }
                entries[op] = operands.ToArray();
                operands.Clear();
            }
            else
            {
                // Operand.
                if (b0 == 28)
                {
                    // 16-bit signed integer
                    var v = (short)((data[i + 1] << 8) | data[i + 2]);
                    operands.Add(v);
                    i += 3;
                }
                else if (b0 == 29)
                {
                    // 32-bit signed integer (DICT only)
                    var v = (int)((data[i + 1] << 24) | (data[i + 2] << 16)
                                | (data[i + 3] << 8)  |  data[i + 4]);
                    operands.Add(v);
                    i += 5;
                }
                else if (b0 == 30)
                {
                    // BCD real number
                    operands.Add(ParseRealBcd(data, ref i));
                }
                else if (b0 is >= 32 and <= 246)
                {
                    operands.Add(b0 - 139);
                    i++;
                }
                else if (b0 is >= 247 and <= 250)
                {
                    operands.Add((b0 - 247) * 256 + data[i + 1] + 108);
                    i += 2;
                }
                else if (b0 is >= 251 and <= 254)
                {
                    operands.Add(-((b0 - 251) * 256) - data[i + 1] - 108);
                    i += 2;
                }
                else
                {
                    // 22..27, 31, 255 are reserved in DICT context — skip safely.
                    i++;
                }
            }
        }
        return new CffDict(entries.ToFrozenDictionary());
    }

    /// <summary>
    /// BCD real: nibble stream terminated by 0xf. Nibble meanings:
    /// 0..9 = digit, a = '.', b = 'E', c = 'E-', d = reserved, e = '-', f = end.
    /// </summary>
    private static double ParseRealBcd(ReadOnlySpan<byte> data, ref int i)
    {
        i++; // skip 0x1e marker (b0 == 30)
        Span<char> buf = stackalloc char[64];
        var len = 0;
        while (true)
        {
            var b = data[i++];
            for (var nibIdx = 0; nibIdx < 2; nibIdx++)
            {
                var nib = nibIdx == 0 ? (b >> 4) & 0xf : b & 0xf;
                if (nib == 0xf) goto done;
                len += AppendNibble(buf[len..], nib);
            }
        }
        done:
        return double.Parse(buf[..len], System.Globalization.CultureInfo.InvariantCulture);

        static int AppendNibble(Span<char> buf, int nib)
        {
            switch (nib)
            {
                case <= 9: buf[0] = (char)('0' + nib); return 1;
                case 0xa: buf[0] = '.'; return 1;
                case 0xb: buf[0] = 'E'; return 1;
                case 0xc: buf[0] = 'E'; buf[1] = '-'; return 2;
                case 0xe: buf[0] = '-'; return 1;
                default: return 0; // reserved
            }
        }
    }
}

/// <summary>Well-known Top DICT operator codes.</summary>
internal static class TopDictOps
{
    public const int Version = 0;
    public const int Notice = 1;
    public const int FullName = 2;
    public const int FamilyName = 3;
    public const int Weight = 4;
    public const int FontBbox = 5;
    public const int Charset = 15;       // offset to charset
    public const int Encoding = 16;      // offset to encoding (CFF1)
    public const int CharStrings = 17;   // offset to CharStrings INDEX
    public const int Private = 18;       // [size, offset] of Private DICT
    public const int Ros = 0x0c1e;       // Registry, Ordering, Supplement → CID
    public const int CidFontVersion = 0x0c1f;
    public const int CidFontRevision = 0x0c20;
    public const int CidFontType = 0x0c21;
    public const int CidCount = 0x0c22;
    public const int FdArray = 0x0c24;   // offset to FDArray INDEX (CID)
    public const int FdSelect = 0x0c25;  // offset to FDSelect (CID)
    public const int CharstringType = 0x0c06; // default 2 (Type 2)
}

/// <summary>Well-known Private DICT operator codes.</summary>
internal static class PrivateDictOps
{
    public const int BlueValues = 6;
    public const int OtherBlues = 7;
    public const int FamilyBlues = 8;
    public const int FamilyOtherBlues = 9;
    public const int Subrs = 19;            // offset to local Subr INDEX (relative to Private DICT start)
    public const int DefaultWidthX = 20;
    public const int NominalWidthX = 21;
}
