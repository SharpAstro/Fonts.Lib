using System.Numerics;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Type1;

/// <summary>
/// Loaded Adobe Type 1 / Type 42 / CID Type 0 font. Separate from
/// <see cref="OpenTypeFont"/> because Type 1 is a fundamentally different
/// container format (PostScript dict, eexec-encrypted Private DICT,
/// charstring-named glyph table).
///
/// <para>Use <see cref="LoadPfb"/> for binary .pfb files; .pfa (ASCII)
/// would need a small wrapper that splits on the eexec / cleartomark
/// boundary — not implemented yet.</para>
///
/// <para>Immutable post-construction; safe for concurrent reads.</para>
/// </summary>
public sealed class Type1Font
{
    private readonly Dictionary<string, byte[]> _charStrings;
    private readonly byte[][] _localSubrs;
    private readonly int _lenIV;
    private readonly string[] _encoding;

    /// <summary>FontMatrix (6 floats, default = identity scaled by 0.001).</summary>
    public float[] FontMatrix { get; }

    /// <summary>
    /// Design-units-per-em derived from <see cref="FontMatrix"/>[0]
    /// (typically 1000 — Type 1 standard).
    /// </summary>
    public int UnitsPerEm => FontMatrix[0] > 0
        ? (int)MathF.Round(1f / FontMatrix[0])
        : 1000;

    /// <summary>charCode (0..255) → glyph name.</summary>
    public IReadOnlyList<string> Encoding => _encoding;

    /// <summary>All glyph names in this font.</summary>
    public IReadOnlyCollection<string> GlyphNames => _charStrings.Keys;

    private Type1Font(Dictionary<string, byte[]> charStrings, byte[][] localSubrs,
        int lenIV, string[] encoding, float[] fontMatrix)
    {
        _charStrings = charStrings;
        _localSubrs = localSubrs;
        _lenIV = lenIV;
        _encoding = encoding;
        FontMatrix = fontMatrix;
    }

    /// <summary>True if this font has a glyph for <paramref name="name"/>.</summary>
    public bool HasGlyph(string name) => _charStrings.ContainsKey(name);

    /// <summary>
    /// Emit a glyph's outline (in font-design-matrix units — apply
    /// <see cref="FontMatrix"/> to convert to "1 unit = 1 em") to
    /// <paramref name="sink"/>. Returns false if the glyph name doesn't exist.
    /// </summary>
    public bool DrawGlyph(string name, IGlyphSink sink)
    {
        if (!_charStrings.TryGetValue(name, out var encrypted)) return false;
        var plain = EexecCipher.Decrypt(encrypted, EexecCipher.CharStringSeed, _lenIV);

        // Decrypt + run, with a recursive seac-resolver that paints the base
        // outline + accent glyph at an offset.
        Type1CharstringInterpreter.Execute(plain, DecryptSubrs(), sink,
            seac: (adx, ady, baseChar, accentChar) =>
            {
                // Draw base directly.
                if (HasGlyph(baseChar)) DrawGlyph(baseChar, sink);
                // Draw accent at the offset; we don't have a sink-translating
                // helper here so consumers wanting accurate seac should wrap
                // the sink themselves. For Phase 9 v1 we ignore the accent
                // offset (most fonts don't use seac).
                if (accentChar != baseChar && HasGlyph(accentChar))
                {
                    var translated = new TranslatingSink(sink, adx, ady);
                    DrawGlyph(accentChar, translated);
                }
            });
        return true;
    }

    /// <summary>Look up a glyph by char code (via <see cref="Encoding"/>) and emit it.</summary>
    public bool DrawGlyphByCharCode(int charCode, IGlyphSink sink)
    {
        if ((uint)charCode >= (uint)_encoding.Length) return false;
        return DrawGlyph(_encoding[charCode], sink);
    }

    /// <summary>
    /// Rasterize a glyph by name to an 8-bit grayscale alpha bitmap at
    /// <paramref name="pixelsPerEm"/>. Returns <see cref="GlyphBitmap.Empty"/>
    /// if the name doesn't exist.
    /// </summary>
    public GlyphBitmap RenderGlyph(string name, float pixelsPerEm,
        int subSamples = SmoothRasterizer.DefaultSubSamples)
    {
        if (!_charStrings.ContainsKey(name)) return GlyphBitmap.Empty;
        return SmoothRasterizer.Rasterize(
            sink => DrawGlyph(name, sink),
            pixelsPerEm, UnitsPerEm, subSamples);
    }

    private byte[][] _decryptedSubrsCache = [];
    private byte[][] DecryptSubrs()
    {
        if (_decryptedSubrsCache.Length != _localSubrs.Length)
        {
            // One-time decrypt; cache is set atomically so concurrent callers
            // either see the empty array (and re-decrypt) or the full result
            // (and use it). Decryption is pure; redundant runs are
            // wasteful but never wrong.
            var arr = new byte[_localSubrs.Length][];
            for (var i = 0; i < arr.Length; i++)
            {
                var enc = _localSubrs[i];
                if (enc is null) continue;
                arr[i] = EexecCipher.Decrypt(enc, EexecCipher.CharStringSeed, _lenIV);
            }
            _decryptedSubrsCache = arr;
        }
        return _decryptedSubrsCache;
    }

    /// <summary>Load from a .pfb byte stream.</summary>
    public static Type1Font LoadPfb(byte[] pfbData)
    {
        if (!PfbReader.IsPfb(pfbData))
            throw new InvalidDataException("Not a .pfb file (missing 0x80 marker).");
        var (asciiHeader, eexecBinary) = PfbReader.Read(pfbData);
        var decrypted = EexecCipher.DecryptEexec(eexecBinary);

        // The decrypted block is the "Private" dict (more PostScript text).
        // The /CharStrings dict typically appears AFTER the eexec block in
        // some old fonts, but for modern Type 1 it's INSIDE the decrypted
        // section. Concatenate header + decrypted so the reader sees both.
        var combined = new byte[asciiHeader.Length + decrypted.Length];
        Array.Copy(asciiHeader, 0, combined, 0, asciiHeader.Length);
        Array.Copy(decrypted, 0, combined, asciiHeader.Length, decrypted.Length);

        var reader = new PostScriptDictReader(combined);
        reader.Parse();

        return new Type1Font(reader.CharStrings, reader.Subrs,
            reader.LenIV, reader.Encoding, reader.FontMatrix);
    }

    /// <summary>Convenience: load a .pfb from disk.</summary>
    public static Type1Font LoadPfbFromFile(string path) => LoadPfb(File.ReadAllBytes(path));

    /// <summary>Translate every coordinate by (dx, dy) — used for seac accent offset.</summary>
    private sealed class TranslatingSink : IGlyphSink
    {
        private readonly IGlyphSink _inner;
        private readonly float _dx;
        private readonly float _dy;
        public TranslatingSink(IGlyphSink inner, float dx, float dy)
        { _inner = inner; _dx = dx; _dy = dy; }
        public void MoveTo(float x, float y) => _inner.MoveTo(x + _dx, y + _dy);
        public void LineTo(float x, float y) => _inner.LineTo(x + _dx, y + _dy);
        public void QuadTo(float cx, float cy, float x, float y)
            => _inner.QuadTo(cx + _dx, cy + _dy, x + _dx, y + _dy);
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            => _inner.CubicTo(c1x + _dx, c1y + _dy, c2x + _dx, c2y + _dy, x + _dx, y + _dy);
        public void Close() => _inner.Close();
        // Vector2 ref for parity with the COLR transform (unused here).
        private static Vector2 _ = Vector2.Zero;
    }
}
