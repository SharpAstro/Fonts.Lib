namespace SharpAstro.Fonts.Type1;

/// <summary>
/// Adobe Type 1 XOR stream cipher used for both eexec section and per-charstring
/// obfuscation. Algorithm (Type 1 spec §7):
///
/// <pre>
/// state = seed
/// for each byte b:
///   plain = b XOR (state &gt;&gt; 8)
///   state = ((state + plain) * 52845 + 22719) &amp; 0xFFFF
/// </pre>
///
/// First N bytes of the decrypted output are random salt and discarded
/// (4 by default for eexec, /lenIV for charstrings).
/// </summary>
internal static class EexecCipher
{
    public const int EexecSeed = 55665;
    public const int CharStringSeed = 4330;
    private const int C1 = 52845;
    private const int C2 = 22719;

    /// <summary>Decrypt a span. Skips <paramref name="discard"/> leading bytes.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> input, int seed, int discard)
    {
        var state = (uint)seed;
        var outLen = input.Length - discard;
        if (outLen < 0) outLen = 0;
        var output = new byte[outLen];
        var oi = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            var plain = (byte)(b ^ (state >> 8));
            state = ((state + b) * C1 + C2) & 0xFFFF;
            if (i >= discard) output[oi++] = plain;
        }
        return output;
    }

    /// <summary>
    /// Decrypt eexec section. Handles both ASCII-hex and binary input by
    /// detecting the format and de-hexing first if needed (per spec, eexec
    /// is hex-encoded if the first 4 chars are all valid hex digits and
    /// followed by whitespace; otherwise binary).
    /// </summary>
    public static byte[] DecryptEexec(ReadOnlySpan<byte> data)
    {
        if (LooksHex(data))
        {
            var binary = Dehex(data);
            return Decrypt(binary, EexecSeed, discard: 4);
        }
        return Decrypt(data, EexecSeed, discard: 4);
    }

    private static bool LooksHex(ReadOnlySpan<byte> data)
    {
        var seen = 0;
        for (var i = 0; i < data.Length && seen < 4; i++)
        {
            var b = data[i];
            if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') continue;
            if (!IsHex(b)) return false;
            seen++;
        }
        return seen == 4;
    }

    private static bool IsHex(byte b)
        => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static byte[] Dehex(ReadOnlySpan<byte> data)
    {
        var ms = new System.IO.MemoryStream(data.Length / 2);
        var have = 0;
        var nibAcc = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') continue;
            if (!IsHex(b)) break; // stop at first non-hex (eexec terminator)
            int nib = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                _ => b - (byte)'A' + 10,
            };
            nibAcc = (nibAcc << 4) | nib;
            have++;
            if (have == 2)
            {
                ms.WriteByte((byte)nibAcc);
                nibAcc = 0;
                have = 0;
            }
        }
        return ms.ToArray();
    }
}
