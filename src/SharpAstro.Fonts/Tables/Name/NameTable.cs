using System.Text;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Name;

/// <summary>Well-known 'name' table name IDs (the ones this parser surfaces by property).</summary>
public enum NameId : ushort
{
    Copyright = 0,
    /// <summary>Family name. Legacy/"Windows-compatible": a style-linked family is limited to
    /// four faces, so extra weights are pushed into separate families ("Noto Sans SemiBold").</summary>
    Family = 1,
    /// <summary>Subfamily — one of Regular/Bold/Italic/Bold Italic under the legacy model.</summary>
    Subfamily = 2,
    UniqueId = 3,
    FullName = 4,
    Version = 5,
    PostScriptName = 6,
    Trademark = 7,
    Manufacturer = 8,
    Designer = 9,
    /// <summary>License description — the full licensing terms, as the foundry states them.
    /// The authoritative answer to "may we redistribute this file", which a file name or a
    /// recollection of where the font came from is not.</summary>
    License = 13,
    /// <summary>URL of the license, where one is published separately (e.g. the OFL text).</summary>
    LicenseUrl = 14,
    /// <summary>Typographic (preferred) family — present only when it differs from
    /// <see cref="Family"/>, i.e. when the family has more than the four style-linked faces.</summary>
    TypographicFamily = 16,
    /// <summary>Typographic (preferred) subfamily — e.g. "SemiBold Condensed Italic".</summary>
    TypographicSubfamily = 17,
}

/// <summary>
/// Parsed 'name' table — the font's own account of what it is called. This is the only
/// authoritative source for a face's family name; deriving one from the file name is guesswork
/// that fails on any face whose file is abbreviated (Segoe UI Symbol lives in seguisym.ttf) and
/// on every face inside a TTC, which has no file name of its own at all.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/name
/// </summary>
public sealed class NameTable
{
    // Decoded strings keyed by name id, already reduced to one best-language pick per id.
    private readonly Dictionary<ushort, string> _byId;

    private NameTable(Dictionary<ushort, string> byId) => _byId = byId;

    /// <summary>The string for <paramref name="id"/>, or null if the font doesn't carry one.</summary>
    public string? Get(NameId id) => _byId.TryGetValue((ushort)id, out var s) ? s : null;

    /// <summary>
    /// The family a user would type: the typographic family when present, else the legacy one.
    /// Prefer this for display; match against <em>both</em> this and <see cref="LegacyFamily"/>
    /// when resolving a caller-supplied name, since either spelling reaches the same file.
    /// </summary>
    public string? Family => Get(NameId.TypographicFamily) ?? Get(NameId.Family);

    /// <summary>The style within <see cref="Family"/> — "Regular", "SemiBold Italic", …</summary>
    public string? Subfamily => Get(NameId.TypographicSubfamily) ?? Get(NameId.Subfamily);

    /// <summary>Name ID 1 specifically (the four-face style-linked family).</summary>
    public string? LegacyFamily => Get(NameId.Family);

    /// <summary>Name ID 2 specifically.</summary>
    public string? LegacySubfamily => Get(NameId.Subfamily);

    /// <summary>Name ID 4 — the full human name, usually "Family Subfamily".</summary>
    public string? FullName => Get(NameId.FullName);

    /// <summary>Name ID 6 — the PostScript name, e.g. "NotoSans-Regular".</summary>
    public string? PostScriptName => Get(NameId.PostScriptName);

    /// <summary>The face's own statement of its licensing terms (name ID 13), or null when it
    /// makes none.</summary>
    public string? License => Get(NameId.License);

    /// <summary>URL of the face's license (name ID 14), or null when it states none.</summary>
    public string? LicenseUrl => Get(NameId.LicenseUrl);

    // Language preference, best first. A font commonly repeats a name in many languages; we
    // want one deterministic pick, and an English one so callers can match against the English
    // family strings that producers and users actually type.
    private const int RankWindowsEnglish = 0;   // platform 3, lang 0x0409
    private const int RankUnicode = 1;          // platform 0 (no language of its own)
    private const int RankWindowsOther = 2;
    private const int RankMacEnglish = 3;       // platform 1, lang 0
    private const int RankOther = 4;

    private static int Rank(ushort platformId, ushort languageId) => platformId switch
    {
        3 => languageId == 0x0409 ? RankWindowsEnglish : RankWindowsOther,
        0 => RankUnicode,
        1 => languageId == 0 ? RankMacEnglish : RankOther,
        // 2 is the deprecated ISO platform. Still emitted in the wild — PDF producers subset
        // fonts down to a lone platform-2 PostScript name — so it must decode, not just rank.
        _ => RankOther,
    };

    public static NameTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt16();
        var count = r.ReadUInt16();
        var storageOffset = r.ReadUInt16();
        _ = version; // format 1 only adds language-tag records after the name records; ignored.

        var byId = new Dictionary<ushort, string>(count);
        var bestRank = new Dictionary<ushort, int>(count);

        for (var i = 0; i < count; i++)
        {
            // A truncated table is a corrupt font, not an exception-worthy event for a resolver
            // scanning every installed file — stop at the last record that fits.
            if (r.Remaining < 12) break;
            var platformId = r.ReadUInt16();
            var encodingId = r.ReadUInt16();
            var languageId = r.ReadUInt16();
            var nameId = r.ReadUInt16();
            var length = r.ReadUInt16();
            var stringOffset = r.ReadUInt16();

            var rank = Rank(platformId, languageId);
            // Cheap rejection before decoding: a worse-ranked duplicate can't win.
            if (bestRank.TryGetValue(nameId, out var have) && have <= rank) continue;

            var start = storageOffset + stringOffset;
            if (start < 0 || length == 0 || start + length > data.Length) continue;

            var text = Decode(data.Slice(start, length), platformId, encodingId);
            if (text is null || text.Length == 0) continue;

            byId[nameId] = text;
            bestRank[nameId] = rank;
        }

        return new NameTable(byId);
    }

    private static string? Decode(ReadOnlySpan<byte> bytes, ushort platformId, ushort encodingId)
    {
        // Platform 1 (Macintosh) encoding 0 is Mac Roman; platform 2 (the deprecated ISO
        // platform) encodings 0 and 2 are ASCII and ISO 8859-1. All three are single-byte and
        // agree with ASCII below 0x80, which is all a font name realistically uses; anything
        // above we leave to the Unicode record that essentially every font also ships (and
        // which outranks these anyway).
        var singleByte = platformId switch
        {
            1 => encodingId == 0,
            2 => encodingId is 0 or 2,
            _ => false,
        };
        if (singleByte)
        {
            Span<char> chars = bytes.Length <= 256 ? stackalloc char[bytes.Length] : new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] > 0x7F) return null;
                chars[i] = (char)bytes[i];
            }
            return new string(chars);
        }
        // Platform 1/2 in any other encoding is a legacy codepage we don't carry tables for.
        if (platformId is 1 or 2 && encodingId != 1) return null;

        // Platform 0 (Unicode), 3 (Windows) and 2/1 (ISO 10646) are UTF-16BE — including Windows
        // encoding 0 ("symbol"), which describes the cmap, not the name strings.
        if (bytes.Length % 2 != 0) return null;
        return Encoding.BigEndianUnicode.GetString(bytes);
    }
}
