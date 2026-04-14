using System.Text;

namespace SharpAstro.Fonts.IO;

/// <summary>
/// 4-byte ASCII tag (e.g. "cmap", "head") stored as a big-endian uint.
/// </summary>
public readonly record struct Tag(uint Value)
{
    public Tag(ReadOnlySpan<char> ascii) : this(FromAscii(ascii)) { }

    public static Tag Parse(string ascii) => new((ReadOnlySpan<char>)ascii);

    private static uint FromAscii(ReadOnlySpan<char> ascii)
    {
        if (ascii.Length != 4)
            throw new ArgumentException("Tag must be exactly 4 ASCII characters.", nameof(ascii));
        return ((uint)(byte)ascii[0] << 24)
             | ((uint)(byte)ascii[1] << 16)
             | ((uint)(byte)ascii[2] << 8)
             |  (byte)ascii[3];
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)((Value >> 24) & 0xFF);
        bytes[1] = (byte)((Value >> 16) & 0xFF);
        bytes[2] = (byte)((Value >> 8) & 0xFF);
        bytes[3] = (byte)(Value & 0xFF);
        return Encoding.ASCII.GetString(bytes);
    }
}

/// <summary>Well-known SFNT table tags.</summary>
internal static class Tags
{
    public static readonly Tag Cmap = Tag.Parse("cmap");
    public static readonly Tag Head = Tag.Parse("head");
    public static readonly Tag Hhea = Tag.Parse("hhea");
    public static readonly Tag Hmtx = Tag.Parse("hmtx");
    public static readonly Tag Maxp = Tag.Parse("maxp");
    public static readonly Tag Name = Tag.Parse("name");
    public static readonly Tag OS2  = Tag.Parse("OS/2");
    public static readonly Tag Post = Tag.Parse("post");
    public static readonly Tag Glyf = Tag.Parse("glyf");
    public static readonly Tag Loca = Tag.Parse("loca");
    public static readonly Tag Cff  = Tag.Parse("CFF ");
    public static readonly Tag Cff2 = Tag.Parse("CFF2");
    public static readonly Tag Colr = Tag.Parse("COLR");
    public static readonly Tag Cpal = Tag.Parse("CPAL");
    public static readonly Tag Cbdt2 = Tag.Parse("CBDT");
    public static readonly Tag Cblc2 = Tag.Parse("CBLC");
    public static readonly Tag Fvar2 = Tag.Parse("fvar");
    public static readonly Tag Avar2 = Tag.Parse("avar");
    public static readonly Tag Gvar2 = Tag.Parse("gvar");
    /// <summary>'cvt ' (with trailing space) — Control Value Table for hinting.</summary>
    public static readonly Tag Cvt2 = Tag.Parse("cvt ");
    public static readonly Tag Fpgm2 = Tag.Parse("fpgm");
    public static readonly Tag Prep2 = Tag.Parse("prep");
    public static readonly Tag Cbdt = Tag.Parse("CBDT");
    public static readonly Tag Cblc = Tag.Parse("CBLC");
    public static readonly Tag Sbix = Tag.Parse("sbix");
    public static readonly Tag Fvar = Tag.Parse("fvar");
    public static readonly Tag Gvar = Tag.Parse("gvar");
    public static readonly Tag Avar = Tag.Parse("avar");
    public static readonly Tag Hvar = Tag.Parse("HVAR");
    public static readonly Tag Mvar = Tag.Parse("MVAR");
    public static readonly Tag Vhea = Tag.Parse("vhea");
    public static readonly Tag Vmtx = Tag.Parse("vmtx");
    public static readonly Tag Kern = Tag.Parse("kern");
    public static readonly Tag Gpos = Tag.Parse("GPOS");
    public static readonly Tag Vvar = Tag.Parse("VVAR");
    /// <summary>'cvar' — CVT Variations table for variable TrueType hinting.</summary>
    public static readonly Tag Cvar = Tag.Parse("cvar");
}
