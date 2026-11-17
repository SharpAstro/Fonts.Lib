using System.Diagnostics;
using System.Text;
using SharpAstro.Fonts.Scenes;
using SharpAstro.Png;
using SharpAstro.Svg;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// <see cref="SceneRasterizer"/> against a browser: an SVG document read by <c>SharpAstro.Svg</c> and drawn here, held
/// pixel for pixel against the same document drawn by headless Chrome or Edge.
/// </summary>
/// <remarks>
/// <para>This is where STROKES get checked. The reader's own tests (in the Codecs repo) compare fills with a browser
/// through a test-only rasteriser that has no stroker; this is the real one.</para>
/// <para>All documents go on one page and are captured in one launch. The comparison lets an edge sit one pixel away
/// (a browser's own curves are a little coarse) and nothing else: a pixel is wrong when it is far from the browser's
/// AND outside what the browser has within a pixel of it.</para>
/// <para>Skipped where no browser is found. Set <c>SVG_BROWSER</c> to an executable to name one.</para>
/// </remarks>
public sealed class SceneRasterizerBrowserTests
{
    private const string Stroke = "fill=\"none\" stroke=\"#024\" stroke-width=\"8\"";

    public static TheoryData<string, string, string> Documents
    {
        get
        {
            TheoryData<string, string, string> documents = Strokes();
            foreach ((string name, string root, string body) in Rung3) documents.Add(name, root, body);
            return documents;
        }
    }

    /// <summary>
    /// A small PNG as a data URI: 8 x 6 pixels in 2 x 2 blocks of flat colour, one row of them half transparent. Blocks,
    /// so that how each renderer blends between pixels when it enlarges the picture changes edges and not areas.
    /// </summary>
    internal static string Picture()
    {
        const int width = 8, height = 6;
        ReadOnlySpan<byte> palette =
        [
            200, 40, 40, 255, 40, 120, 200, 255, 250, 200, 30, 255, 30, 150, 80, 255,
            120, 40, 160, 255, 240, 240, 240, 255, 20, 20, 20, 255, 240, 130, 30, 255,
            0, 90, 160, 128, 200, 0, 100, 128, 0, 0, 0, 128, 255, 255, 255, 128,
        ];
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                palette.Slice((y / 2 * 4 + x / 2) * 4, 4).CopyTo(rgba.AsSpan((y * width + x) * 4));
            }
        }
        return "data:image/png;base64," + Convert.ToBase64String(SharpAstro.Png.PngWriter.Encode(rgba, width, height));
    }

    /// <summary>
    /// What rung 3 of the reader added to the scene: clips, masks, markers, <c>paint-order</c>, patterns and images. The same
    /// documents the reader is held to in the Codecs repo, drawn here by the real rasteriser.
    /// </summary>
    internal static readonly (string Name, string Root, string Body)[] Rung3 =
    [
        ("stroke", "width=\"160\" height=\"110\"", """
            <path d="M10 15 H50" stroke="#c00" stroke-width="8"/>
            <path d="M10 30 H50" stroke="#080" stroke-width="8" stroke-linecap="round"/>
            <path d="M10 45 H50" stroke="#00c" stroke-width="8" stroke-linecap="square"/>
            <path d="M70 40 L85 10 L100 40" fill="none" stroke="#444" stroke-width="6"/>
            <path d="M110 40 L125 10 L140 40" fill="none" stroke="#a60" stroke-width="6" stroke-linejoin="bevel"/>
            <path d="M70 80 L85 55 L100 80" fill="none" stroke="#06a" stroke-width="6" stroke-linejoin="round"/>
            <path d="M112 80 L120 52 L128 80" fill="none" stroke="#a0a" stroke-width="6" stroke-miterlimit="2"/>
            <path d="M10 60 C 30 50 40 90 60 70" fill="none" stroke="#333" stroke-width="3" stroke-dasharray="6 3 2 3" stroke-dashoffset="4"/>
            <rect x="10" y="85" width="30" height="16" fill="#fc0" stroke="#036" stroke-width="4" stroke-linejoin="round"/>
            <circle cx="60" cy="95" r="8" fill="none" stroke="#090" stroke-width="2" transform="translate(-40 0) scale(1.8 1)"/>
            <path d="M135 60 l12 0 l0 12 l-12 0 z" fill="none" stroke="#c06" stroke-width="5"/>
            <path d="M135 92 h0" stroke="#000" stroke-width="8" stroke-linecap="round"/>
            <path d="M150 92 h0" stroke="#000" stroke-width="8" stroke-linecap="square"/>
            """),
        ("clip-path", "width=\"170\" height=\"110\"", """
            <defs>
            <clipPath id="c1"><circle cx="30" cy="30" r="20"/></clipPath>
            <clipPath id="c2" clipPathUnits="objectBoundingBox"><path d="M0 0 H1 L0.3 1 Z"/></clipPath>
            <clipPath id="c3"><path d="M110 5 h50 v50 h-50 z M122 12 h26 v26 h-26 z" clip-rule="evenodd"/></clipPath>
            <clipPath id="c4" transform="translate(0 60) rotate(10)"><rect x="12" y="2" width="30" height="20"/><circle cx="50" cy="20" r="12"/></clipPath>
            <clipPath id="c5"><rect x="65" y="62" width="40" height="40" clip-path="url(#c5a)"/></clipPath>
            <clipPath id="c5a"><circle cx="70" cy="70" r="26"/></clipPath>
            <clipPath id="c6" clip-path="url(#c6a)"><rect x="112" y="62" width="50" height="40"/></clipPath>
            <clipPath id="c6a"><ellipse cx="130" cy="82" rx="30" ry="14"/></clipPath>
            </defs>
            <rect x="5" y="5" width="50" height="50" fill="#c33" clip-path="url(#c1)"/>
            <rect x="60" y="8" width="40" height="44" fill="#36c" clip-path="url(#c2)"/>
            <rect x="105" y="0" width="60" height="60" fill="#084" clip-path="url(#c3)"/>
            <g clip-path="url(#c4)"><rect x="0" y="60" width="60" height="50" fill="#fa0"/><circle cx="20" cy="80" r="14" fill="#606" opacity="0.6"/></g>
            <rect x="60" y="58" width="50" height="50" fill="#555" clip-path="url(#c5)"/>
            <rect x="108" y="58" width="60" height="50" fill="#b08" stroke="#000" stroke-width="3" clip-path="url(#c6)"/>
            """),
        ("viewport-clip", "width=\"140\" height=\"70\"", """
            <svg x="5" y="5" width="50" height="40" viewBox="0 0 25 20"><circle cx="20" cy="16" r="10" fill="#c33"/><rect x="-5" y="-5" width="12" height="12" fill="#36c"/></svg>
            <svg x="65" y="5" width="30" height="40" overflow="visible"><circle cx="28" cy="30" r="12" fill="#084"/></svg>
            <defs>
            <symbol id="s" viewBox="0 0 10 10"><circle cx="9" cy="5" r="5" fill="#a0a"/><path d="M0 0 L12 12" stroke="#000" stroke-width="2"/></symbol>
            <path id="q" d="M0 0 h30 l-20 20 z"/>
            <clipPath id="u"><use href="#q" x="4" transform="translate(100 46)"/></clipPath>
            </defs>
            <use href="#s" x="105" y="8" width="28" height="28"/>
            <rect x="95" y="40" width="45" height="30" fill="#f60" clip-path="url(#u)"/>
            """),
        ("mask", "width=\"170\" height=\"110\"", """
            <defs>
            <linearGradient id="fade"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient>
            <mask id="m1"><rect x="5" y="5" width="50" height="45" fill="url(#fade)"/></mask>
            <mask id="m2" style="mask-type: alpha"><circle cx="80" cy="28" r="20" fill="#f00" fill-opacity="0.6"/></mask>
            <mask id="m3" maskContentUnits="objectBoundingBox"><rect width="1" height="1" fill="#888"/><circle cx="0.4" cy="0.5" r="0.3" fill="#fff"/></mask>
            <mask id="m4" maskUnits="userSpaceOnUse" x="10" y="62" width="30" height="40"><rect x="0" y="55" width="60" height="55" fill="#fff"/></mask>
            <mask id="m5" x="0" y="0" width="0.6" height="1"><rect x="60" y="55" width="110" height="55" fill="#0f0"/></mask>
            <mask id="m6"><rect x="115" y="60" width="50" height="45" fill="#fff"/><circle cx="136" cy="82" r="12" fill="#000"/></mask>
            </defs>
            <rect x="5" y="5" width="50" height="45" fill="#06c" mask="url(#m1)"/>
            <rect x="60" y="5" width="50" height="45" fill="#080" mask="url(#m2)"/>
            <rect x="115" y="5" width="50" height="45" fill="#c00" mask="url(#m3)"/>
            <rect x="5" y="60" width="50" height="45" fill="#850" mask="url(#m4)"/>
            <rect x="60" y="60" width="50" height="45" fill="#508" mask="url(#m5)"/>
            <g mask="url(#m6)" opacity="0.8"><rect x="115" y="60" width="30" height="45" fill="#f80"/><rect x="135" y="60" width="30" height="45" fill="#08f"/></g>
            """),
        ("markers", "width=\"180\" height=\"120\"", """
            <defs>
            <marker id="arrow" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto"><path d="M0 0 L6 3 L0 6 Z" fill="#c00"/></marker>
            <marker id="dot" markerWidth="4" markerHeight="4" refX="2" refY="2"><circle cx="2" cy="2" r="1.5" fill="#06c"/></marker>
            <marker id="tri" viewBox="0 0 10 10" markerWidth="12" markerHeight="8" refX="0" refY="5" orient="auto-start-reverse" markerUnits="userSpaceOnUse"><path d="M0 0 L10 5 L0 10 Z" fill="#080"/></marker>
            <marker id="big" markerWidth="3" markerHeight="3" refX="1" refY="1" orient="30" overflow="visible"><rect width="3" height="2" fill="#a0a" fill-opacity="0.7"/><rect x="2.5" width="2" height="1" fill="#000"/></marker>
            <marker id="clipped" markerWidth="3" markerHeight="3" refX="1.5" refY="1.5" orient="auto"><circle cx="2.5" cy="1.5" r="1.5" fill="#f80"/></marker>
            </defs>
            <line x1="10" y1="12" x2="60" y2="22" stroke="#333" stroke-width="2" marker-start="url(#dot)" marker-end="url(#arrow)"/>
            <polyline points="80,25 100,8 120,25 140,8 165,20" fill="none" stroke="#333" stroke-width="2" marker-start="url(#arrow)" marker-mid="url(#arrow)" marker-end="url(#arrow)"/>
            <path d="M10 60 C 30 30 50 90 70 55 A 20 12 20 0 1 105 70 Q 120 40 130 62" fill="none" stroke="#555" stroke-width="1.5" marker-start="url(#tri)" marker-mid="url(#dot)" marker-end="url(#tri)"/>
            <path d="M145 40 l25 5 l-10 25 z" fill="#eee" stroke="#333" stroke-width="2" marker-start="url(#arrow)" marker-mid="url(#arrow)" marker-end="url(#dot)"/>
            <path d="M10 100 L40 85 M55 85 L80 105 L100 90" fill="none" stroke="#333" stroke-width="2" style="marker: url(#arrow)"/>
            <polygon points="120,90 140,82 150,105" fill="none" stroke="#333" stroke-width="3" marker-mid="url(#big)" marker-start="url(#clipped)"/>
            """),
        ("paint-order", "width=\"150\" height=\"60\"", """
            <defs><marker id="m" markerWidth="4" markerHeight="4" refX="2" refY="2"><circle cx="2" cy="2" r="2" fill="#000"/></marker></defs>
            <circle cx="25" cy="30" r="18" fill="#fc0" stroke="#036" stroke-width="10"/>
            <circle cx="75" cy="30" r="18" fill="#fc0" stroke="#036" stroke-width="10" paint-order="stroke"/>
            <path d="M105 12 L140 12 L140 45" fill="none" stroke="#c00" stroke-width="6" marker-mid="url(#m)" marker-start="url(#m)" paint-order="markers"/>
            <circle cx="118" cy="40" r="10" fill="#0a0" stroke="#000" stroke-width="6" paint-order="stroke fill" opacity="0.5"/>
            """),
        ("pattern", "width=\"170\" height=\"110\"", """
            <defs>
            <pattern id="p1" width="0.25" height="0.25"><rect width="6" height="6" fill="#c33"/><circle cx="9" cy="8" r="3" fill="#36c"/></pattern>
            <pattern id="p2" patternUnits="userSpaceOnUse" x="3" y="2" width="12" height="10"><path d="M0 0 L12 10" stroke="#084" stroke-width="2"/><rect width="4" height="4" fill="#fa0"/></pattern>
            <pattern id="p3" patternUnits="userSpaceOnUse" width="10" height="10" patternTransform="rotate(30) scale(1.5 1)"><rect width="5" height="10" fill="#606"/></pattern>
            <pattern id="p4" patternUnits="userSpaceOnUse" width="16" height="12" viewBox="0 0 4 4"><circle cx="2" cy="2" r="1.8" fill="#066"/></pattern>
            <pattern id="p5" width="0.5" height="0.5" patternContentUnits="objectBoundingBox"><rect width="0.25" height="0.25" fill="#b08"/><rect x="0.25" y="0.25" width="0.2" height="0.25" fill="#333"/></pattern>
            <pattern id="p6" href="#p2" patternTransform="skewX(20)"/>
            </defs>
            <rect x="5" y="5" width="48" height="48" fill="url(#p1)"/>
            <rect x="60" y="5" width="50" height="48" fill="url(#p2)" stroke="#000"/>
            <circle cx="140" cy="29" r="24" fill="url(#p3)"/>
            <rect x="5" y="60" width="50" height="45" fill="url(#p4)"/>
            <rect x="60" y="60" width="50" height="45" fill="url(#p5)"/>
            <path d="M120 65 h40 v35 h-40 z" fill="#eee" stroke="url(#p6)" stroke-width="8"/>
            """),
        // What the specification leaves open, or says in a way a reader could take two ways, one case a cell (30 x 30,
        // six across). Several are errors, so issues are expected; what matters is what is still drawn.
        ("rung3-edges", "width=\"180\" height=\"150\"", """
            <defs>
            <marker id="ar" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#c00"/></marker>
            <marker id="hid" display="none" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#06c"/></marker>
            <marker id="faint" opacity="0.3" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto"><path d="M0 0 L4 2 L0 4 Z" fill="#080"/></marker>
            <clipPath id="bb" clipPathUnits="objectBoundingBox"><rect width="1" height="1"/></clipPath>
            <clipPath id="bb2" clipPathUnits="objectBoundingBox"><circle cx="0.5" cy="0.5" r="0.5"/></clipPath>
            <clipPath id="loop"><rect width="20" height="20" clip-path="url(#loop)"/></clipPath>
            <clipPath id="withg"><g><rect width="12" height="12"/></g><circle cx="19" cy="19" r="6"/><rect x="14" width="12" height="8" visibility="hidden"/></clipPath>
            <clipPath id="circ"><circle cx="16" cy="14" r="10"/></clipPath>
            <clipPath id="empty"/>
            <mask id="zero" width="0"><rect width="100" height="100" fill="#fff"/></mask>
            <mask id="mm" mask="url(#half)"><rect width="28" height="28" fill="#fff"/></mask>
            <mask id="half"><rect width="14" height="28" fill="#fff"/></mask>
            <mask id="ml"><rect width="20" height="28" fill="#fff" mask="url(#ml)"/></mask>
            <marker id="mloop" markerWidth="6" markerHeight="6" refX="3" refY="3" overflow="visible"><circle cx="3" cy="3" r="2" fill="#c00"/><path d="M0 1 H5" stroke="#00c" stroke-width="1" marker-end="url(#mloop)"/></marker>
            <pattern id="ploop" patternUnits="userSpaceOnUse" width="8" height="8"><rect width="8" height="8" fill="url(#ploop) #c0c"/><rect width="4" height="4" fill="#fc0"/></pattern>
            <mask id="mop" opacity="0.4"><rect width="28" height="28" fill="#fff"/></mask>
            <pattern id="pop" opacity="0.4" patternUnits="userSpaceOnUse" width="8" height="8"><rect width="5" height="5" fill="#900"/></pattern>
            <mask id="mta" mask-type="alpha"><rect width="20" height="28" fill="#000" fill-opacity="0.5"/></mask>
            <rect id="ub" width="20" height="20" fill="#0aa"/>
            <clipPath id="circ2"><circle cx="10" cy="10" r="9"/></clipPath>
            <clipPath id="circ3"><circle cx="7" cy="6" r="5"/></clipPath>
            </defs>
            <g transform="translate(0 0)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker="url(#ar)"/></g>
            <g transform="translate(30 0)"><rect x="2" y="2" width="24" height="24" fill="#c33" clip-path="url(#nope)"/></g>
            <g transform="translate(60 0)"><rect x="2" y="2" width="24" height="24" fill="#36c" mask="url(#nope)"/></g>
            <g transform="translate(90 0)"><path d="M2 14 H26" stroke="#084" stroke-width="6" clip-path="url(#bb)"/></g>
            <g transform="translate(120 0)"><rect x="2" y="2" width="24" height="24" fill="#a0a" clip-path="url(#loop)"/></g>
            <g transform="translate(150 0)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#ar)" visibility="hidden"/></g>
            <g transform="translate(0 30)"><rect x="2" y="2" width="24" height="24" fill="#f80" clip-path="url(#withg)"/></g>
            <g transform="translate(30 30)"><rect x="2" y="2" width="24" height="24" fill="#066" mask="url(#zero)"/></g>
            <g transform="translate(60 30)"><rect x="4" y="4" width="18" height="18" fill="none" stroke="#333" marker-start="url(#ar)" marker-mid="url(#ar)"/></g>
            <g transform="translate(90 30)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#hid)"/></g>
            <g transform="translate(120 30)"><path d="M4 14 H22" stroke="none" stroke-width="3" marker-end="url(#ar)"/></g>
            <g transform="translate(150 30)"><rect x="2" y="2" width="24" height="24" fill="#555" clip-path="url(#ar)"/></g>
            <g transform="translate(0 60)"><rect x="2" y="2" width="24" height="24" fill="#840" mask="url(#mm)"/></g>
            <g transform="translate(30 60)"><svg x="2" y="2" width="24" height="24" clip-path="url(#circ)"><rect width="24" height="24" fill="#c0c"/></svg></g>
            <g transform="translate(60 60)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#faint)"/></g>
            <g transform="translate(90 60)"><rect x="2" y="2" width="24" height="24" fill="#08a" mask="url(#ml)"/></g>
            <g transform="translate(120 60)"><g clip-path="url(#bb2)"><rect x="4" y="4" width="10" height="10" fill="#c33" transform="rotate(20 9 9)"/><circle cx="20" cy="20" r="6" fill="#36c"/></g></g>
            <g transform="translate(150 60)"><rect x="2" y="2" width="24" height="24" fill="#333" clip-path="url(#empty)"/></g>
            <g transform="translate(0 90)"><path d="M4 14 H22" stroke="#333" stroke-width="2" marker-end="url(#mloop)"/></g>
            <g transform="translate(30 90)"><rect x="2" y="2" width="24" height="24" fill="url(#ploop) #0a0"/></g>
            <g transform="translate(60 90)"><rect x="2" y="2" width="24" height="24" fill="#036" mask="url(#mop)"/></g>
            <g transform="translate(90 90)"><rect x="2" y="2" width="24" height="24" fill="url(#pop)"/></g>
            <g transform="translate(120 90)"><rect x="2" y="2" width="24" height="24" fill="#036" mask="url(#mta)"/></g>
            <g transform="translate(150 90)"><use href="#ub" x="6" y="4" clip-path="url(#circ2)"/></g>
            <g transform="translate(0 120)"><svg x="2" y="2" width="24" height="24" viewBox="0 0 12 12" clip-path="url(#circ3)"><rect width="12" height="12" fill="#770"/></svg></g>
            """),
        ("image", "width=\"170\" height=\"70\"", $"""
            <rect width="170" height="70" fill="#ddd"/>
            <image x="5" y="5" width="48" height="60" href="{Picture()}"/>
            <image x="60" y="5" width="48" height="60" preserveAspectRatio="xMinYMax slice" href="{Picture()}"/>
            <image x="115" y="5" width="50" height="25" preserveAspectRatio="none" opacity="0.6" href="{Picture()}"/>
            <image width="16" height="12" transform="translate(120 38) rotate(15) scale(2)" href="{Picture()}"/>
            """),
    ];

    /// <summary>Documents that are in error on purpose: their issues are not required to be empty.</summary>
    private static readonly HashSet<string> InError = ["rung3-edges"];

    private static TheoryData<string, string, string> Strokes() => new()
    {
        { "caps", "width=\"120\" height=\"70\"",
            $"<path d=\"M15 15 H105\" {Stroke} stroke-linecap=\"butt\"/><path d=\"M15 35 H105\" {Stroke} stroke-linecap=\"round\"/>" +
            $"<path d=\"M15 55 H105\" {Stroke} stroke-linecap=\"square\"/><path d=\"M60 5 V65\" stroke=\"#c00\" stroke-width=\"1\"/>" },
        { "joins", "width=\"150\" height=\"70\"",
            $"<path d=\"M10 60 L30 15 L50 60\" {Stroke} stroke-linejoin=\"miter\"/><path d=\"M60 60 L80 15 L100 60\" {Stroke} stroke-linejoin=\"round\"/>" +
            $"<path d=\"M110 60 L125 15 L140 60\" {Stroke} stroke-linejoin=\"bevel\"/>" },
        { "miter-limit", "width=\"150\" height=\"70\"",
            // The same sharp corner three times: within the default limit of 4 it is not, so it is bevelled; a limit of 10 lets it through.
            $"<path d=\"M10 60 L25 10 L40 60\" {Stroke}/><path d=\"M60 60 L75 10 L90 60\" {Stroke} stroke-miterlimit=\"10\"/>" +
            $"<path d=\"M105 60 L125 30 L145 60\" {Stroke} stroke-miterlimit=\"1\"/>" },
        { "closed-shapes", "width=\"150\" height=\"70\"",
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"50\" fill=\"#fc0\" stroke=\"#024\" stroke-width=\"6\"/>" +
            "<circle cx=\"80\" cy=\"35\" r=\"20\" fill=\"none\" stroke=\"#a03\" stroke-width=\"5\"/>" +
            "<polygon points=\"115,10 145,35 115,60\" fill=\"#9cf\" stroke=\"#024\" stroke-width=\"4\" stroke-linejoin=\"round\"/>" },
        { "curves", "width=\"150\" height=\"80\"",
            $"<path d=\"M10 70 C 10 10 70 10 70 40 S 130 70 140 15\" {Stroke} stroke-linecap=\"round\"/>" +
            "<path d=\"M10 20 Q 40 -10 60 25 T 110 20\" fill=\"none\" stroke=\"#c60\" stroke-width=\"3\"/>" +
            "<path d=\"M90 70 A 20 10 20 1 1 130 60\" fill=\"none\" stroke=\"#090\" stroke-width=\"4\" stroke-linecap=\"square\"/>" },
        { "dashes", "width=\"150\" height=\"80\"",
            "<path d=\"M10 12 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"12 6\"/>" +
            "<path d=\"M10 28 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"12 6\" stroke-dashoffset=\"9\"/>" +
            "<path d=\"M10 44 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"1 10\" stroke-linecap=\"round\"/>" +
            "<path d=\"M10 60 H140\" stroke=\"#024\" stroke-width=\"6\" stroke-dasharray=\"14 4 2 4\" stroke-linecap=\"square\" stroke-dashoffset=\"-5\"/>" +
            "<circle cx=\"125\" cy=\"62\" r=\"12\" fill=\"none\" stroke=\"#a03\" stroke-width=\"3\" stroke-dasharray=\"5 3\"/>" },
        { "stroke-transforms", "width=\"150\" height=\"80\"",
            // A stroke is distorted by a transform that is not uniform, and scaled by one that is.
            "<circle r=\"10\" fill=\"none\" stroke=\"#024\" stroke-width=\"3\" transform=\"translate(40 40) scale(3 1)\"/>" +
            "<rect x=\"-10\" y=\"-10\" width=\"20\" height=\"20\" fill=\"#fc0\" stroke=\"#a03\" stroke-width=\"2\" transform=\"translate(110 40) rotate(25) skewX(20) scale(1.5)\"/>" },
        { "stroke-paints", "width=\"150\" height=\"80\"",
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient></defs>" +
            "<rect x=\"12\" y=\"12\" width=\"50\" height=\"56\" fill=\"#ff8\" stroke=\"url(#g)\" stroke-width=\"10\"/>" +
            "<circle cx=\"110\" cy=\"40\" r=\"24\" fill=\"#09c\" stroke=\"#000\" stroke-width=\"10\" stroke-opacity=\"0.5\"/>" +
            "<path d=\"M80 70 L140 70\" stroke=\"#0a0\" stroke-width=\"8\" opacity=\"0.5\" stroke-linecap=\"round\"/>" },
        { "outline-icon", "width=\"96\" height=\"96\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#123\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"",
            // The kind of icon a stroke-based set is made of.
            "<path d=\"M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z\"/><path d=\"M12 6v6l4 2\"/><path d=\"M3 21l3-3M21 21l-3-3\"/>" },
        { "fills-and-layers", "width=\"150\" height=\"80\"",
            "<defs><radialGradient id=\"r\" fx=\"0.3\" fy=\"0.3\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
            "<linearGradient id=\"l\" x2=\"0.25\" spreadMethod=\"reflect\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"1\" stop-color=\"#fc0\"/></linearGradient></defs>" +
            "<rect x=\"5\" y=\"5\" width=\"60\" height=\"70\" fill=\"url(#r)\"/><path d=\"M75 5 L145 5 L110 40 Z\" fill=\"url(#l)\"/>" +
            "<path d=\"M110 42 L122 78 L92 56 L128 56 L98 78 Z\" fill=\"#a0a\" fill-rule=\"evenodd\"/>" +
            "<g opacity=\"0.5\"><circle cx=\"30\" cy=\"45\" r=\"22\" fill=\"#e00\"/><circle cx=\"55\" cy=\"45\" r=\"22\" fill=\"#e00\"/></g>" },
    };

    private static readonly Lazy<Dictionary<string, (int Width, int Height, byte[] Rgb)>> Rendered = new(RenderAll);

    [Theory]
    [MemberData(nameof(Documents))]
    public void TheRasteriser_MatchesTheBrowser(string name, string root, string body)
    {
        if (Browser.Path is null)
        {
            Assert.Skip($"No Chromium-family browser found. Set {Browser.Variable} to its executable, or install Chrome or Edge.");
            return;
        }

        SvgDocument document = SvgReader.Parse(Wrap(body, root));
        if (!InError.Contains(name)) document.Issues.ShouldBeEmpty(name);
        (int width, int height, byte[] theirs) = Rendered.Value[name];
        ((int)MathF.Round(document.Width), (int)MathF.Round(document.Height)).ShouldBe((width, height));

        ColorBitmap ours = SceneRasterizer.Rasterize(document.Scene, width, height);
        // Over white, as the page is.
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int a = ours.Pixels[i * 4 + 3];
            for (int ch = 0; ch < 3; ch++) rgb[i * 3 + ch] = (byte)((ours.Pixels[i * 4 + ch] * a + 255 * (255 - a) + 127) / 255);
        }

        double sum = 0;
        int wrong = 0;
        var where = new StringBuilder();
        for (int i = 0; i < width * height; i++)
        {
            int worst = 0;
            for (int ch = 0; ch < 3; ch++)
            {
                int d = Math.Abs(rgb[i * 3 + ch] - theirs[i * 3 + ch]);
                sum += d;
                worst = Math.Max(worst, d);
            }
            if (worst > MaxPixelDifference && OutsideNeighbourhood(rgb, theirs, width, height, i) && wrong++ < 6)
            {
                where.Append($" ({i % width},{i / width}: ours {rgb[i * 3]},{rgb[i * 3 + 1]},{rgb[i * 3 + 2]} theirs {theirs[i * 3]},{theirs[i * 3 + 1]},{theirs[i * 3 + 2]})");
            }
        }
        double mean = sum / (width * height * 3);
        string report = $"{name}: mean {mean:0.000}, pixels off {wrong} of {width * height}{where}";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        if (Environment.GetEnvironmentVariable("SVG_ORACLE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            PngWriter.WriteRgba(System.IO.Path.Combine(dump, name + "-ours.png"), ours.Pixels, width, height);
        }
        mean.ShouldBeLessThan(MaxMeanDifference, report);
        wrong.ShouldBe(0, report);
    }

    /// <summary>Of 255. A pixel further off than this is not an anti-aliasing difference.</summary>
    private const int MaxPixelDifference = 72;

    /// <summary>Of 255. How far outside its neighbourhood's range a pixel may be.</summary>
    private const int EdgeSlack = 24;

    /// <summary>Of 255, over all pixels and channels.</summary>
    private const double MaxMeanDifference = 1.2;

    private static bool OutsideNeighbourhood(byte[] ours, byte[] theirs, int width, int height, int i)
    {
        int x = i % width, y = i / width;
        for (int ch = 0; ch < 3; ch++)
        {
            int low = 255, high = 0;
            for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
            {
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    int v = theirs[(ny * width + nx) * 3 + ch];
                    (low, high) = (Math.Min(low, v), Math.Max(high, v));
                }
            }
            int value = ours[i * 3 + ch];
            if (value < low - EdgeSlack || value > high + EdgeSlack) return true;
        }
        return false;
    }

    private static string Wrap(string body, string root) => $"<svg xmlns=\"http://www.w3.org/2000/svg\" {root}>{body}</svg>";

    private static Dictionary<string, (int, int, byte[])> RenderAll()
    {
        const int gap = 4;
        var tiles = new List<(string Name, int Y, int Width, int Height)>();
        var html = new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#fff\">");
        int y = 0, pageWidth = 1;
        foreach (var row in Documents)
        {
            (string name, string root, string body) = row.Data;
            string svg = Wrap(body, root);
            SvgDocument document = SvgReader.Parse(svg);
            int width = (int)MathF.Round(document.Width), height = (int)MathF.Round(document.Height);
            html.Append($"<img style=\"position:absolute;left:0;top:{y}px\" src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}\">");
            tiles.Add((name, y, width, height));
            y += height + gap;
            pageWidth = Math.Max(pageWidth, width + gap);
        }
        html.Append("</body></html>");

        byte[] page = Browser.Capture(html.ToString(), pageWidth, y);
        var rendered = new Dictionary<string, (int, int, byte[])>();
        foreach ((string name, int top, int width, int height) in tiles)
        {
            byte[] rgb = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                for (int x = 0; x < width; x++)
                {
                    int from = ((top + row) * pageWidth + x) * 4;
                    page.AsSpan(from, 3).CopyTo(rgb.AsSpan((row * width + x) * 3));
                }
            }
            rendered[name] = (width, height, rgb);
        }
        return rendered;
    }

    /// <summary>Finds a Chromium-family browser and has it capture a page, headless. RGBA bytes, <c>width</c> wide.</summary>
    private static class Browser
    {
        public const string Variable = "SVG_BROWSER";

        public static readonly string? Path = Resolve();

        public static byte[] Capture(string html, int width, int height)
        {
            string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpastro-fonts-svg-oracle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string page = System.IO.Path.Combine(work, "page.html"), shot = System.IO.Path.Combine(work, "page.png");
                File.WriteAllText(page, html);
                var start = new ProcessStartInfo(Path!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string argument in (string[])
                [
                    "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars", "--no-first-run",
                    "--disable-extensions", "--disable-background-networking",
                    "--force-device-scale-factor=1", "--force-color-profile=srgb",
                    $"--user-data-dir={System.IO.Path.Combine(work, "profile")}",
                    $"--window-size={width},{height}",
                    $"--screenshot={shot}",
                    new Uri(page).AbsoluteUri,
                ])
                {
                    start.ArgumentList.Add(argument);
                }

                // The capture is done when the browser says so on stderr, not when it exits: now and then a browser
                // writes the whole screenshot and never exits. Wait for the message or the exit, give a finished
                // browser a moment to leave, and end it if it does not. Everything waits on an event or a timer.
                var log = new StringBuilder();
                var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (log) log.AppendLine(e.Data);
                    if (e.Data.Contains("written to file", StringComparison.Ordinal)) written.TrySetResult();
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    Task exited = process.WaitForExitAsync(TestContext.Current.CancellationToken);
                    Task.WhenAny(exited, written.Task, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)).GetAwaiter().GetResult();
                    if (written.Task.IsCompleted) exited.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                    if (!(exited.IsCompleted || written.Task.IsCompleted) || !File.Exists(shot))
                    {
                        string said;
                        lock (log) said = log.ToString();
                        throw new InvalidOperationException($"{Path} made no screenshot. {said}");
                    }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(TimeSpan.FromSeconds(10));
                    }
                }

                PngImageDecoder.TryDecodeRgba8(File.ReadAllBytes(shot), out byte[]? rgba, out int w, out int h).ShouldBeTrue();
                w.ShouldBe(width);
                h.ShouldBeGreaterThanOrEqualTo(height);
                return rgba!;
            }
            finally
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string? Resolve()
        {
            if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named) return File.Exists(named) ? named : null;

            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (string? root in (string?[])
                    [Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LocalAppData")])
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    candidates.Add(System.IO.Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                    candidates.Add(System.IO.Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
                candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            }
            else
            {
                foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (string name in (string[])["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge"])
                    {
                        candidates.Add(System.IO.Path.Combine(directory, name));
                    }
                }
            }
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
