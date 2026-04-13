using System.Globalization;
using System.Text;

namespace SharpAstro.Fonts.Type1;

/// <summary>
/// Tiny PostScript scanner that extracts the bits of a Type 1 font dictionary
/// we actually need: <c>/lenIV</c>, <c>/FontMatrix</c>, <c>/FontBBox</c>,
/// <c>/Encoding</c>, and the <c>/CharStrings</c> + <c>/Subrs</c> blocks
/// (each as raw bytes per glyph / index).
///
/// <para>NOT a full PostScript interpreter — pattern-matches the conventional
/// Type 1 emit format that every real-world Type 1 font follows.</para>
/// </summary>
internal sealed class PostScriptDictReader
{
    private readonly byte[] _data;
    public int LenIV { get; private set; } = 4;
    public float[] FontMatrix { get; } = [0.001f, 0, 0, 0.001f, 0, 0];
    public Dictionary<string, byte[]> CharStrings { get; } = new(64);
    /// <summary>Subroutine index → raw (still-encrypted) bytes.</summary>
    public byte[][] Subrs { get; private set; } = [];
    /// <summary>charCode (0..255) → glyph name; unmapped entries are ".notdef".</summary>
    public string[] Encoding { get; } = MakeStandardEncoding();

    public PostScriptDictReader(byte[] data) => _data = data;

    public void Parse()
    {
        // Look for /lenIV NN def
        ScanInt("/lenIV", out var lenIV); if (lenIV > 0) LenIV = lenIV;

        // /FontMatrix [a b c d e f]
        ScanFloatArray("/FontMatrix", FontMatrix);

        // /Encoding — handle either "StandardEncoding def" or "256 array …
        // dup IDX /name put …"
        ScanEncoding();

        // /Subrs  N array …  dup IDX SIZE  -| <SIZE bytes> |- NP
        ScanSubrs();

        // /CharStrings  N dict dup begin  /name SIZE -| <SIZE bytes> |- ND
        ScanCharStrings();
    }

    // ---- Field scanners ----------------------------------------------------

    private void ScanInt(string key, out int value)
    {
        value = 0;
        var pos = IndexOf(key);
        if (pos < 0) return;
        pos += key.Length;
        SkipWhitespace(ref pos);
        var sb = new StringBuilder(8);
        while (pos < _data.Length && _data[pos] is (byte)'-' or >= (byte)'0' and <= (byte)'9')
            sb.Append((char)_data[pos++]);
        if (sb.Length > 0) int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void ScanFloatArray(string key, float[] dest)
    {
        var pos = IndexOf(key);
        if (pos < 0) return;
        pos += key.Length;
        SkipWhitespace(ref pos);
        if (pos >= _data.Length || _data[pos] != '[') return;
        pos++;
        var i = 0;
        while (pos < _data.Length && _data[pos] != ']' && i < dest.Length)
        {
            SkipWhitespace(ref pos);
            var sb = new StringBuilder(16);
            while (pos < _data.Length && _data[pos] is (byte)'-' or (byte)'.' or (byte)'+' or (byte)'e' or (byte)'E'
                or >= (byte)'0' and <= (byte)'9')
                sb.Append((char)_data[pos++]);
            if (sb.Length > 0
                && float.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                dest[i++] = v;
            SkipWhitespace(ref pos);
        }
    }

    private void ScanEncoding()
    {
        // Look for "/Encoding".
        var pos = IndexOf("/Encoding");
        if (pos < 0) return;
        pos += "/Encoding".Length;
        SkipWhitespace(ref pos);
        // Either "StandardEncoding def" (already initialized) or "256 array".
        if (Match(pos, "StandardEncoding")) return;
        if (!Match(pos, "256")) return;
        // Find subsequent "dup INDEX /name put" entries until "readonly" / "def".
        var endMarker = IndexOfFrom("readonly", pos);
        if (endMarker < 0) endMarker = IndexOfFrom("def", pos);
        if (endMarker < 0) endMarker = _data.Length;
        var span = pos;
        while (span < endMarker)
        {
            var dup = IndexOfFrom("dup ", span);
            if (dup < 0 || dup >= endMarker) break;
            var p = dup + 4;
            // Read int idx
            var sb = new StringBuilder(4);
            while (p < endMarker && _data[p] is >= (byte)'0' and <= (byte)'9') sb.Append((char)_data[p++]);
            if (sb.Length == 0) { span = dup + 4; continue; }
            var idx = int.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            SkipWhitespace(ref p);
            if (p >= endMarker || _data[p] != '/') { span = p; continue; }
            p++;
            var name = ReadName(ref p);
            if ((uint)idx < (uint)Encoding.Length) Encoding[idx] = name;
            span = p;
        }
    }

    private void ScanSubrs()
    {
        var pos = IndexOf("/Subrs");
        if (pos < 0) return;
        pos += "/Subrs".Length;
        SkipWhitespace(ref pos);
        // Read count
        var sb = new StringBuilder(8);
        while (pos < _data.Length && _data[pos] is >= (byte)'0' and <= (byte)'9') sb.Append((char)_data[pos++]);
        if (sb.Length == 0) return;
        var count = int.Parse(sb.ToString(), CultureInfo.InvariantCulture);
        Subrs = new byte[count][];

        // Each entry: "dup IDX SIZE -| <SIZE bytes> |- NP" or "dup IDX SIZE RD <data> NP"
        var p = pos;
        for (var n = 0; n < count; n++)
        {
            var dup = IndexOfFrom("dup ", p);
            if (dup < 0) break;
            p = dup + 4;
            var idxSb = new StringBuilder(4);
            while (p < _data.Length && _data[p] is >= (byte)'0' and <= (byte)'9') idxSb.Append((char)_data[p++]);
            if (idxSb.Length == 0) break;
            var idx = int.Parse(idxSb.ToString(), CultureInfo.InvariantCulture);
            SkipWhitespace(ref p);
            var sizeSb = new StringBuilder(8);
            while (p < _data.Length && _data[p] is >= (byte)'0' and <= (byte)'9') sizeSb.Append((char)_data[p++]);
            if (sizeSb.Length == 0) break;
            var size = int.Parse(sizeSb.ToString(), CultureInfo.InvariantCulture);
            // Skip token (RD or -|) then exactly one space then `size` bytes.
            SkipWhitespace(ref p);
            // The RD operator name varies; advance past one token then one space.
            while (p < _data.Length && _data[p] != ' ' && _data[p] != '\t' && _data[p] != '\r' && _data[p] != '\n') p++;
            if (p < _data.Length) p++; // single space separator
            if (p + size > _data.Length) break;
            var bytes = new byte[size];
            Array.Copy(_data, p, bytes, 0, size);
            if ((uint)idx < (uint)Subrs.Length) Subrs[idx] = bytes;
            p += size;
        }
    }

    private void ScanCharStrings()
    {
        var pos = IndexOf("/CharStrings");
        if (pos < 0) return;
        pos += "/CharStrings".Length;
        SkipWhitespace(ref pos);
        // Optional count + dict.
        while (pos < _data.Length && _data[pos] is >= (byte)'0' and <= (byte)'9') pos++;
        // Walk entries until "end".
        var p = pos;
        while (p < _data.Length)
        {
            var slash = IndexOfFrom("/", p);
            if (slash < 0) break;
            p = slash + 1;
            var name = ReadName(ref p);
            if (string.IsNullOrEmpty(name)) continue;
            SkipWhitespace(ref p);
            // Read size
            var sb = new StringBuilder(8);
            while (p < _data.Length && _data[p] is >= (byte)'0' and <= (byte)'9') sb.Append((char)_data[p++]);
            if (sb.Length == 0) continue;
            var size = int.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            SkipWhitespace(ref p);
            // Skip RD / -| token then one space.
            while (p < _data.Length && _data[p] != ' ' && _data[p] != '\t' && _data[p] != '\r' && _data[p] != '\n') p++;
            if (p < _data.Length) p++;
            if (p + size > _data.Length) break;
            var bytes = new byte[size];
            Array.Copy(_data, p, bytes, 0, size);
            CharStrings[name] = bytes;
            p += size;
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private int IndexOf(string token) => IndexOfFrom(token, 0);

    private int IndexOfFrom(string token, int start)
    {
        var bytes = Encoding<byte>.ConvertAsciiToBytes(token);
        for (var i = start; i + bytes.Length <= _data.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < bytes.Length; k++)
                if (_data[i + k] != bytes[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private bool Match(int pos, string token)
    {
        var bytes = Encoding<byte>.ConvertAsciiToBytes(token);
        if (pos + bytes.Length > _data.Length) return false;
        for (var k = 0; k < bytes.Length; k++)
            if (_data[pos + k] != bytes[k]) return false;
        return true;
    }

    private void SkipWhitespace(ref int pos)
    {
        while (pos < _data.Length && _data[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            pos++;
    }

    private string ReadName(ref int pos)
    {
        var start = pos;
        while (pos < _data.Length)
        {
            var b = _data[pos];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'
                or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
                or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}') break;
            pos++;
        }
        return System.Text.Encoding.ASCII.GetString(_data, start, pos - start);
    }

    private static string[] MakeStandardEncoding()
    {
        // We only need a default shape; charcode → glyph name lookup for
        // Type 1 typically comes from the font's own /Encoding override.
        var arr = new string[256];
        for (var i = 0; i < arr.Length; i++) arr[i] = ".notdef";
        return arr;
    }
}

// Tiny helper so we don't depend on System.Text.Encoding for trivial ASCII work.
internal static class Encoding<T>
{
    public static byte[] ConvertAsciiToBytes(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }
}
