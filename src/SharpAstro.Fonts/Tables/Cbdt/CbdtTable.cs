using SharpAstro.Fonts.Color;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Tables.Cblc;

namespace SharpAstro.Fonts.Tables.Cbdt;

/// <summary>
/// Parsed 'CBDT' (Color Bitmap Data) table — holds PNG payload per glyph
/// indexed via <see cref="CblcTable"/>.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/cbdt</para>
///
/// <para>Holds a <see cref="ReadOnlyMemory{Byte}"/> view of the table —
/// PNG decoding happens on demand per render.</para>
/// </summary>
public sealed class CbdtTable
{
    private readonly ReadOnlyMemory<byte> _data;

    public CbdtTable(ReadOnlyMemory<byte> data) => _data = data;

    /// <summary>
    /// Resolve a glyph's image bytes + decoded metrics. Returns null if the
    /// glyph has no entry in <paramref name="strike"/>.
    /// </summary>
    public CbdtImage? GetImage(BitmapStrike strike, uint gid)
    {
        var sub = strike.FindSubtable(gid);
        if (sub is null) return null;
        var (off, len) = sub.LocateImage(gid);
        if (len == 0) return null;

        var imgSpan = _data.Span;
        if (off + len > (uint)imgSpan.Length) return null;
        var slice = _data.Slice((int)off, (int)len);

        // Decode metrics + payload by image format.
        int pngStart;
        int height, width;
        int bearingX, bearingY;
        int advance;

        switch (sub.ImageFormat)
        {
            case BitmapImageFormat.SmallMetricsPng:
            {
                // smallMetrics (5 bytes) + dataLength(uint32) + PNG
                var s = slice.Span;
                height = s[0];
                width = s[1];
                bearingX = (sbyte)s[2];
                bearingY = (sbyte)s[3];
                advance = s[4];
                var dataLen = ((uint)s[5] << 24) | ((uint)s[6] << 16) | ((uint)s[7] << 8) | s[8];
                pngStart = 9;
                _ = dataLen;
                break;
            }
            case BitmapImageFormat.BigMetricsPng:
            {
                // bigMetrics (8 bytes) + dataLength(uint32) + PNG
                var s = slice.Span;
                height = s[0];
                width = s[1];
                bearingX = (sbyte)s[2];   // hori bearing X
                bearingY = (sbyte)s[3];   // hori bearing Y
                advance = s[4];           // hori advance
                // s[5..7] = vert bearingX/Y/advance — ignored
                var dataLen = ((uint)s[8] << 24) | ((uint)s[9] << 16) | ((uint)s[10] << 8) | s[11];
                pngStart = 12;
                _ = dataLen;
                break;
            }
            case BitmapImageFormat.PngOnly:
            {
                // dataLength(uint32) + PNG. Metrics from index subtable's
                // shared bigMetrics (format-2-only) — only useful for fonts
                // whose CBLC is index format 2.
                var s = slice.Span;
                var dataLen = ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
                pngStart = 4;
                _ = dataLen;
                if (sub.ConstBigMetrics is { Length: 8 } m)
                {
                    height = m[0];
                    width = m[1];
                    bearingX = (sbyte)m[2];
                    bearingY = (sbyte)m[3];
                    advance = m[4];
                }
                else
                {
                    height = width = 0;
                    bearingX = bearingY = advance = 0;
                }
                break;
            }
            default:
                return null;
        }

        var png = slice[pngStart..];
        return new CbdtImage(png, width, height, bearingX, bearingY, advance);
    }
}

/// <summary>
/// One per-glyph CBDT entry: the encoded PNG bytes + the bitmap metrics
/// (in pixels at the chosen strike's PPEM).
/// </summary>
public readonly record struct CbdtImage(
    ReadOnlyMemory<byte> Png,
    int Width, int Height,
    int BearingX, int BearingY,
    int Advance);
