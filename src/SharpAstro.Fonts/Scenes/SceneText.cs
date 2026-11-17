using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Outlines;
using SharpAstro.VectorGraphics;

namespace SharpAstro.Fonts.Scenes;

/// <summary>
/// Finds the face for a font a scene asks for, and for one character of it: the caller's font lookup, whatever it is
/// (a folder of files, the system's fonts, one bundled face). Null when there is none; the character is then not drawn.
/// </summary>
/// <param name="font">What the text asks for: family names in order, size, weight, slant, stretch.</param>
/// <param name="character">The character to be written. A lookup with fallback fonts answers by character.</param>
public delegate OpenTypeFont? SceneFontResolver(VectorFont font, Rune character);

/// <summary>One glyph of a shaped run.</summary>
/// <param name="Font">The face the glyph is in.</param>
/// <param name="GlyphId">The glyph.</param>
/// <param name="Cluster">Where the character (or the first of the characters) the glyph stands for starts in the text, as a UTF-16 offset. Glyphs of one cluster are placed together.</param>
/// <param name="Advance">How far the pen moves after the glyph, in the design units of <paramref name="Font"/>, kerning included.</param>
/// <param name="OffsetX">A shift of the glyph from the pen, in design units, to the right.</param>
/// <param name="OffsetY">A shift of the glyph from the pen, in design units, upwards.</param>
public readonly record struct SceneGlyph(OpenTypeFont Font, uint GlyphId, int Cluster, float Advance, float OffsetX = 0, float OffsetY = 0);

/// <summary>
/// Turns a run of text in one font into glyphs, in the order they are written: the caller's text shaping. A scene's
/// text is placed by <see cref="SceneText"/>, which asks this for each span and does nothing about fonts itself.
/// </summary>
/// <param name="text">The characters of one span.</param>
/// <param name="font">The font the span asks for.</param>
public delegate IReadOnlyList<SceneGlyph> SceneTextShaper(string text, VectorFont font);

/// <summary>
/// Lays out the text of a scene (<see cref="VectorText"/>) and turns it into the shapes of its glyphs, which is how a
/// scene's text is drawn: <see cref="SceneRasterizer"/> does this for every text node when it is given a shaper.
/// </summary>
/// <remarks>
/// <para>The placing is SVG's: each span follows the one before it on a line; a character may be given a place of its
/// own, a shift or a turn; the characters from one absolute place to the next are a chunk, anchored at its start, its
/// middle or its end; text on a path is bent along it. What the characters look like and how wide they are is the
/// shaper's word, and the font is the resolver's: nothing here chooses either.</para>
/// <para>Glyph outlines are drawn, so a colour font's glyphs come out in the text's paint, not their own.</para>
/// </remarks>
public static class SceneText
{
    /// <summary>A shaper that writes everything in one face.</summary>
    public static SceneTextShaper ShaperFor(OpenTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return ShaperFor((_, _) => font);
    }

    /// <summary>
    /// A plain shaper over a font lookup: one glyph a character by the face's character map, its advance, and pair
    /// kerning between neighbours of one face. No ligatures, no joining, no reordering: it is right for Latin, Greek and
    /// Cyrillic labels and wrong for scripts that need more. A caller with a real shaper passes that as the
    /// <see cref="SceneTextShaper"/> itself.
    /// </summary>
    public static SceneTextShaper ShaperFor(SceneFontResolver resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return (text, font) =>
        {
            var glyphs = new List<SceneGlyph>(text.Length);
            OpenTypeFont? usual = null;
            int at = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                int cluster = at;
                at += rune.Utf16SequenceLength;
                // The face of the first character serves the rest, as long as it has them: the lookup is asked again
                // only for a character it does not hold.
                OpenTypeFont? face = usual ??= resolve(font, rune);
                if (face is null) continue;
                uint glyph = face.GetGlyphId((uint)rune.Value);
                if (glyph == 0)
                {
                    OpenTypeFont? other = resolve(font, rune);
                    if (other is not null && !ReferenceEquals(other, face) && other.GetGlyphId((uint)rune.Value) is var found and not 0)
                    {
                        (face, glyph) = (other, found);
                    }
                }
                if (face.Hmtx is null) continue;
                if (glyphs.Count > 0 && ReferenceEquals(glyphs[^1].Font, face))
                {
                    int kern = face.GetKerning(glyphs[^1].GlyphId, glyph);
                    if (kern != 0) glyphs[^1] = glyphs[^1] with { Advance = glyphs[^1].Advance + kern };
                }
                glyphs.Add(new SceneGlyph(face, glyph, cluster, face.Hmtx.GetAdvanceWidth(glyph)));
            }
            return glyphs;
        };
    }

    /// <summary>
    /// The scene with every piece of text (in groups, masks and pattern tiles too) replaced by the shapes of its
    /// glyphs. What comes back holds no <see cref="VectorText"/> and needs no font to draw.
    /// </summary>
    public static VectorScene WithTextAsShapes(VectorScene scene, SceneTextShaper shape)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(shape);
        return new VectorScene(scene.Width, scene.Height, Replace(scene.Nodes, shape, 0));
    }

    private static ImmutableArray<VectorNode> Replace(ImmutableArray<VectorNode> nodes, SceneTextShaper shape, int depth)
    {
        if (depth > 256) throw new NotSupportedException("The scene nests more than 256 deep.");
        var result = ImmutableArray.CreateBuilder<VectorNode>(nodes.Length);
        foreach (VectorNode node in nodes)
        {
            switch (node)
            {
                case VectorText text:
                    result.AddRange(ToShapes(text, shape));
                    break;
                case VectorGroup group:
                    result.Add(new VectorGroup
                    {
                        Id = group.Id, Opacity = group.Opacity, Clip = group.Clip,
                        Mask = group.Mask is { } mask ? new VectorMask { Kind = mask.Kind, Children = Replace(mask.Children, shape, depth + 1) } : null,
                        Children = Replace(group.Children, shape, depth + 1),
                    });
                    break;
                case VectorShape { Fill.Paint: PatternPaint } or VectorShape { Stroke.Paint: PatternPaint }:
                {
                    var painted = (VectorShape)node;
                    result.Add(new VectorShape
                    {
                        Id = painted.Id, Path = painted.Path, Transform = painted.Transform,
                        Fill = painted.Fill is { } fill ? fill with { Paint = InPaint(fill.Paint) } : null,
                        Stroke = painted.Stroke is { } stroke ? stroke with { Paint = InPaint(stroke.Paint) } : null,
                    });
                    break;
                }
                default:
                    result.Add(node);
                    break;
            }
        }
        return result.ToImmutable();

        VectorPaint InPaint(VectorPaint paint) =>
            paint is PatternPaint pattern ? pattern with { Children = Replace(pattern.Children, shape, depth + 1) } : paint;
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------

    /// <summary>A character as it is placed: the glyphs that stand for it, how far it moves the pen, and where it ends up.</summary>
    private sealed class Cluster
    {
        public int Span;
        public List<(OpenTypeFont Font, uint GlyphId, float X, float Y, float Scale)> Glyphs = [];

        /// <summary>The move of the pen, in the text's units: the glyphs' advances and the span's spacing.</summary>
        public float Advance;

        /// <summary>Where its origin is on the baseline, and how far it is turned (radians, clockwise in y-down coordinates).</summary>
        public float X, Y, Turn;

        /// <summary>What the source says about it, until the pen is run: a place of its own, and shifts.</summary>
        public float? PlaceX, PlaceY;
        public float ShiftX, ShiftY;

        public bool ChunkStart, Hidden, AfterPath;
        public VectorTextPath? Path;
    }

    /// <summary>The lines of a face at a size, in the text's units: how far it reaches above and below the baseline, and the height of an x.</summary>
    private readonly record struct Metrics(float Ascent, float Descent, float XHeight);

    /// <summary>
    /// The text as shapes, in drawing order: for every span that is drawn, the outlines of its glyphs (and its lines) as
    /// one path with the span's paints. Empty when nothing is to be drawn.
    /// </summary>
    public static ImmutableArray<VectorNode> ToShapes(VectorText text, SceneTextShaper shape)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(shape);
        ImmutableArray<VectorTextSpan> spans = text.Spans.IsDefault ? [] : text.Spans;

        // 1. Every span shaped, its glyphs gathered by the character they stand for.
        var clusters = new List<Cluster>();
        var metrics = new Metrics[spans.Length];
        var faces = new OpenTypeFont?[spans.Length];
        for (int s = 0; s < spans.Length; s++)
        {
            VectorTextSpan span = spans[s];
            IReadOnlyList<SceneGlyph> glyphs = shape(span.Text, span.Font) ?? [];
            ImmutableArray<VectorTextPosition> positions = span.Positions.IsDefault ? [] : span.Positions;
            int next = 0;
            for (int g = 0; g < glyphs.Count;)
            {
                SceneGlyph first = glyphs[g];
                var cluster = new Cluster { Span = s, Path = span.Path };
                float pen = 0;
                int end = g;
                for (; end < glyphs.Count && glyphs[end].Cluster == first.Cluster; end++)
                {
                    SceneGlyph glyph = glyphs[end];
                    float scale = span.Font.Size / Math.Max(1, (int)glyph.Font.UnitsPerEm);
                    cluster.Glyphs.Add((glyph.Font, glyph.GlyphId, pen + glyph.OffsetX * scale, -glyph.OffsetY * scale, scale));
                    pen += glyph.Advance * scale;
                }
                // Up to where the next glyph's characters start: a ligature takes the places of all its characters.
                int until = end < glyphs.Count ? glyphs[end].Cluster : span.Text.Length;
                bool space = first.Cluster < span.Text.Length && span.Text[first.Cluster] is ' ' or ' ';
                cluster.Advance = pen + span.LetterSpacing + (space ? span.WordSpacing : 0);

                // What the source says about this character. A place and a turn are the first character's; shifts add up.
                float? x = null, y = null;
                float dx = 0, dy = 0;
                for (; next < positions.Length && positions[next].Index < until; next++)
                {
                    VectorTextPosition position = positions[next];
                    if (position.Index < first.Cluster) continue;
                    if (position.Index == first.Cluster)
                    {
                        (x, y) = (position.X, position.Y);
                        cluster.Turn = position.Rotate * (MathF.PI / 180f);
                    }
                    dx += position.Dx;
                    dy += position.Dy;
                }
                cluster.ChunkStart = x is not null || y is not null;
                (cluster.PlaceX, cluster.PlaceY, cluster.ShiftX, cluster.ShiftY) = (x, y, dx, dy);
                clusters.Add(cluster);
                g = end;
            }
            faces[s] = glyphs.Count > 0 ? glyphs[0].Font : null;
            metrics[s] = faces[s] is { } face ? MetricsOf(face, span.Font.Size) : default;
        }
        if (clusters.Count == 0) return [];

        // 2. The length asked for: more room between characters, or everything stretched along the line.
        float stretch = 1;
        if (text.TextLength is { } wanted && wanted >= 0)
        {
            float natural = 0;
            foreach (Cluster cluster in clusters) natural += cluster.Advance;
            if (text.LengthAdjust == TextLengthAdjust.SpacingAndGlyphs)
            {
                if (natural > 0) stretch = wanted / natural;
                foreach (Cluster cluster in clusters) cluster.Advance *= stretch;
            }
            else if (clusters.Count > 1)
            {
                float more = (wanted - natural) / (clusters.Count - 1);
                for (int i = 0; i < clusters.Count - 1; i++) clusters[i].Advance += more;
            }
        }

        // 3. The pen. A chunk starts with the text, with a character that has a place of its own, where a path begins
        // and where one ends.
        float penX = 0, penY = 0;
        VectorTextPath? on = null;
        var paths = new Dictionary<VectorTextPath, Flattened>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < clusters.Count; i++)
        {
            Cluster cluster = clusters[i];
            if (!ReferenceEquals(cluster.Path, on))
            {
                if (cluster.Path is not null)
                {
                    // Along a path the pen is a distance along it and a distance off it, both from nothing.
                    (penX, penY) = (0, 0);
                }
                else
                {
                    // After a path the text goes on from the path's end.
                    Flattened left = Flatten(on!, paths);
                    (penX, penY) = (left.End.X, left.End.Y);
                    cluster.AfterPath = true;
                }
                cluster.ChunkStart = true;
                on = cluster.Path;
            }
            if (i == 0) cluster.ChunkStart = true;
            if (cluster.PlaceX is { } placeX) penX = placeX;
            // Along a path there is one coordinate to be given, and x gives it.
            if (cluster.PlaceY is { } placeY && cluster.Path is null) penY = placeY;
            penX += cluster.ShiftX;
            penY += cluster.ShiftY;
            (cluster.X, cluster.Y) = (penX, penY);
            penX += cluster.Advance;
        }

        // 4. Each chunk moved as a whole, so that its anchor is where the chunk was told to start.
        for (int start = 0; start < clusters.Count;)
        {
            int end = start + 1;
            while (end < clusters.Count && !clusters[end].ChunkStart) end++;
            TextAnchor anchor = spans[clusters[start].Span].Anchor;
            if (anchor != TextAnchor.Start)
            {
                float a = float.PositiveInfinity, b = float.NegativeInfinity;
                for (int i = start; i < end; i++)
                {
                    a = MathF.Min(a, MathF.Min(clusters[i].X, clusters[i].X + clusters[i].Advance));
                    b = MathF.Max(b, MathF.Max(clusters[i].X, clusters[i].X + clusters[i].Advance));
                }
                float shift = clusters[start].X - (anchor == TextAnchor.Middle ? (a + b) / 2 : b);
                for (int i = start; i < end; i++) clusters[i].X += shift;
            }
            start = end;
        }

        // 5. Characters on a path: the middle of each goes to its distance along the path, turned to the path's direction.
        foreach (Cluster cluster in clusters)
        {
            if (cluster.Path is not { } along) continue;
            Flattened path = Flatten(along, paths);
            if (path.Length <= 0)
            {
                cluster.Hidden = true;
                continue;
            }
            float offset = along.StartIsFraction ? along.StartOffset * path.Length : along.StartOffset;
            float middle = cluster.X + cluster.Advance / 2 + offset;
            // A character whose middle is off the path is not drawn. That holds for a closed path too: SVG 2 lets text
            // that starts part of the way round a closed path go on past the path's start, but a browser stops it at
            // the path's end as it always did (measured), and so does this.
            if (middle < 0 || middle > path.Length)
            {
                cluster.Hidden = true;
                continue;
            }
            path.At(middle, out Vector2 point, out Vector2 tangent);
            var normal = new Vector2(-tangent.Y, tangent.X);
            Vector2 origin = point - tangent * (cluster.Advance / 2) + normal * cluster.Y;
            (cluster.X, cluster.Y) = (origin.X, origin.Y);
            cluster.Turn += MathF.Atan2(tangent.Y, tangent.X);
        }

        // 6. The box of the whole text, for paints in its units: of the characters' cells, not of their ink.
        Vector2 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        foreach (Cluster cluster in clusters)
        {
            if (cluster.Path is not null && cluster.Hidden) continue;
            Metrics m = metrics[cluster.Span];
            Matrix3x2 frame = Frame(cluster, spans[cluster.Span], m);
            foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[new(0, -m.Ascent), new(cluster.Advance, -m.Ascent), new(0, m.Descent), new(cluster.Advance, m.Descent)])
            {
                Vector2 p = Vector2.Transform(corner, frame);
                low = Vector2.Min(low, p);
                high = Vector2.Max(high, p);
            }
        }
        Matrix3x2 box = high.X > low.X && high.Y > low.Y
            ? new Matrix3x2(high.X - low.X, 0, 0, high.Y - low.Y, low.X, low.Y)
            : Matrix3x2.Identity;

        // 7. The lines: each set once, under every span that is under it, and so in one piece where spans meet.
        var nodes = ImmutableArray.CreateBuilder<VectorNode>();
        var outlines = new Dictionary<(OpenTypeFont, uint), VectorPath>();
        var builder = new PathBuilder();
        var through = new List<VectorNode>();
        var drawn = new List<VectorTextLine>();
        foreach (VectorTextSpan span in spans)
        {
            foreach (VectorTextLine line in span.Lines.IsDefault ? [] : span.Lines)
            {
                if (drawn.Exists(known => ReferenceEquals(known, line))) continue;
                drawn.Add(line);
                VectorFill? lineFill = line.Fill is { } lf ? new VectorFill(line.FillInBox ? InBox(lf.Paint, box) : lf.Paint, lf.Opacity, FillRule.NonZero) : null;
                VectorStroke? lineStroke = line.Stroke is { } ls ? ls with { Paint = line.StrokeInBox ? InBox(ls.Paint, box) : ls.Paint } : null;
                if (lineFill is null && lineStroke is null) continue;

                // An underline and an overline lie under the glyphs, a line through them over.
                builder.Clear();
                Lines(builder, line, line.Kinds & (TextDecorations.Underline | TextDecorations.Overline), clusters, spans, metrics, faces, outlines, stretch);
                if (builder.ToPath() is { IsEmpty: false } under)
                {
                    nodes.Add(new VectorShape { Path = under, Transform = text.Transform, Fill = lineFill, Stroke = lineStroke });
                }
                builder.Clear();
                Lines(builder, line, line.Kinds & TextDecorations.LineThrough, clusters, spans, metrics, faces, outlines, stretch);
                if (builder.ToPath() is { IsEmpty: false } over)
                {
                    through.Add(new VectorShape { Path = over, Transform = text.Transform, Fill = lineFill, Stroke = lineStroke });
                }
            }
        }

        // 8. The glyphs: a span's as one path.
        string? id = text.Id;
        for (int s = 0, c = 0; s < spans.Length; s++)
        {
            VectorTextSpan span = spans[s];
            builder.Clear();
            for (; c < clusters.Count && clusters[c].Span == s; c++)
            {
                Cluster cluster = clusters[c];
                if (cluster.Hidden || span.Hidden) continue;
                Matrix3x2 frame = Frame(cluster, span, metrics[s]);
                foreach ((OpenTypeFont font, uint glyph, float x, float y, float scale) in cluster.Glyphs)
                {
                    VectorPath outline = OutlineOf(font, glyph, outlines);
                    if (outline.IsEmpty) continue;
                    // Design units, y up, to the cluster's frame; then the frame to the text's units.
                    outline.Replay(new Placed(builder, new Matrix3x2(scale * stretch, 0, 0, -scale, x * stretch, y) * frame));
                }
            }
            VectorPath path = builder.ToPath();
            if (path.IsEmpty) continue;

            VectorFill? fill = span.Fill is { } f ? new VectorFill(span.FillInBox ? InBox(f.Paint, box) : f.Paint, f.Opacity, FillRule.NonZero) : null;
            VectorStroke? stroke = span.Stroke is { } k ? k with { Paint = span.StrokeInBox ? InBox(k.Paint, box) : k.Paint } : null;
            if (fill is null && stroke is null) continue;
            if (span.StrokeFirst && fill is not null && stroke is not null)
            {
                nodes.Add(new VectorShape { Id = id, Path = path, Transform = text.Transform, Stroke = stroke });
                nodes.Add(new VectorShape { Path = path, Transform = text.Transform, Fill = fill });
            }
            else
            {
                nodes.Add(new VectorShape { Id = id, Path = path, Transform = text.Transform, Fill = fill, Stroke = stroke });
            }
            id = null;
        }
        nodes.AddRange(through);
        return nodes.ToImmutable();
    }

    /// <summary>
    /// A character's own coordinates (its origin on the alphabetic baseline, x along the line, y down) to the text's:
    /// moved off the y it was given by the span's baseline and shift, turned, and put at its place.
    /// </summary>
    private static Matrix3x2 Frame(Cluster cluster, VectorTextSpan span, Metrics metrics) =>
        Frame(cluster, Down(span.Baseline, span.BaselineShift, span.ScriptRise, metrics, span.Font.Size));

    private static Matrix3x2 Frame(Cluster cluster, float down)
    {
        Matrix3x2 frame = Matrix3x2.CreateTranslation(0, down);
        if (cluster.Turn != 0) frame *= Matrix3x2.CreateRotation(cluster.Turn);
        return frame * Matrix3x2.CreateTranslation(cluster.X, cluster.Y);
    }

    /// <summary>
    /// How far below the y it was given the alphabetic baseline of some text lies: by the line of the font that is to lie
    /// on that y, less what the text is raised. The factors are a browser's (measured), where CSS leaves them open.
    /// </summary>
    private static float Down(TextBaseline baseline, float shift, float scriptRise, Metrics metrics, float size)
    {
        float down = baseline switch
        {
            TextBaseline.Middle => metrics.XHeight / 2,
            TextBaseline.Central => (metrics.Ascent - metrics.Descent) / 2,
            TextBaseline.Hanging => metrics.Ascent * 0.8f,
            TextBaseline.Mathematical => metrics.Ascent * 0.5f,
            TextBaseline.TextTop => metrics.Ascent,
            TextBaseline.Ideographic or TextBaseline.TextBottom => -metrics.Descent,
            _ => 0,
        };
        // A superscript is raised by half the height of a line of its own size, a subscript lowered by it.
        float script = size > 0 ? scriptRise / size * (metrics.Ascent + metrics.Descent) / 2 : 0;
        return down - shift - script;
    }

    /// <summary>A paint in the units of the text's box, now that the box is known.</summary>
    private static VectorPaint InBox(VectorPaint paint, Matrix3x2 box) => paint switch
    {
        LinearGradientPaint linear => linear with { Transform = linear.Transform * box },
        RadialGradientPaint radial => radial with { Transform = radial.Transform * box },
        _ => paint,
    };

    /// <summary>
    /// One set of lines, of the kinds asked for, under every character that is under it. Characters of one size that
    /// follow one another straight along a line share one bar; a character that is turned, moved off the line, of
    /// another size or set on a path has a piece of its own.
    /// </summary>
    /// <remarks>
    /// Where the bars go is a browser's, measured (CSS leaves it to the user agent), and it follows the span a bar runs
    /// under, not the element that asked for the line: a bar is a tenth of the span's font
    /// size thick; an underline starts max(1, half its thickness rounded up) below the baseline; an overline ends on
    /// the font's ascent taken to a whole unit; a line through the text is centred a third of that ascent above the
    /// baseline. An underline and an overline are broken where a glyph crosses them, by the bar's thickness to each
    /// side of the glyph's outline.
    /// </remarks>
    private static void Lines(PathBuilder builder, VectorTextLine line, TextDecorations kinds, List<Cluster> clusters, ImmutableArray<VectorTextSpan> spans,
        Metrics[] metrics, OpenTypeFont?[] faces, Dictionary<(OpenTypeFont, uint), VectorPath> outlines, float stretch)
    {
        if (kinds == TextDecorations.None) return;
        var gaps = new List<(float From, float To)>();

        for (int start = 0; start < clusters.Count;)
        {
            if (!Under(clusters[start]))
            {
                start++;
                continue;
            }
            // As far as the characters go on straight from one another, at one size and on one baseline.
            Cluster first = clusters[start];
            VectorTextSpan under = spans[first.Span];
            float size = under.Font.Size, thickness = size / 10;
            Metrics own = metrics[first.Span];
            float down = Down(under.Baseline, under.BaselineShift, under.ScriptRise, own, size);
            int end = start + 1;
            while (end < clusters.Count && Under(clusters[end]) && first.Turn == 0 && clusters[end].Turn == 0
                && first.Path is null && clusters[end].Path is null && !clusters[end].ChunkStart
                && MathF.Abs(clusters[end].Y - first.Y) < 1e-3f
                && MathF.Abs(clusters[end].X - (clusters[end - 1].X + clusters[end - 1].Advance)) < 1e-3f * MathF.Max(1, size)
                && SameLine(spans[clusters[end].Span], metrics[clusters[end].Span]))
            {
                end++;
            }
            Cluster last = clusters[end - 1];
            float length = last.X + last.Advance - first.X;

            if (faces[first.Span] is not null && length > 0 && thickness > 0)
            {
                Matrix3x2 frame = Frame(first, down);
                float ascent = MathF.Round(own.Ascent);
                if ((kinds & TextDecorations.Underline) != 0) Bar(MathF.Max(1, MathF.Ceiling(thickness / 2)), skipInk: true);
                if ((kinds & TextDecorations.Overline) != 0) Bar(-ascent - MathF.Floor(thickness), skipInk: true);
                if ((kinds & TextDecorations.LineThrough) != 0) Bar(-ascent / 3 - thickness / 2, skipInk: false);

                void Bar(float top, bool skipInk)
                {
                    gaps.Clear();
                    if (skipInk)
                    {
                        // Where the outlines of the glyphs on this stretch are inside the bar, in the bar's own frame.
                        for (int i = start; i < end; i++)
                        {
                            Cluster cluster = clusters[i];
                            foreach ((OpenTypeFont font, uint glyph, float x, float y, float scale) in cluster.Glyphs)
                            {
                                VectorPath outline = OutlineOf(font, glyph, outlines);
                                if (outline.IsEmpty) continue;
                                var place = new Matrix3x2(scale * stretch, 0, 0, -scale, x * stretch + cluster.X - first.X, y);
                                Crossings(outline, place, top, top + thickness, gaps);
                            }
                        }
                        for (int i = 0; i < gaps.Count; i++) gaps[i] = (gaps[i].From - thickness, gaps[i].To + thickness);
                        gaps.Sort();
                    }

                    float from = 0;
                    foreach ((float gapFrom, float gapTo) in gaps)
                    {
                        if (gapFrom > from) Piece(from, MathF.Min(gapFrom, length));
                        from = MathF.Max(from, gapTo);
                        if (from >= length) break;
                    }
                    if (from < length) Piece(from, length);

                    void Piece(float left, float right)
                    {
                        if (right <= left) return;
                        // Wound the same way every time, so that pieces that overlap add up under the non-zero rule.
                        ReadOnlySpan<Vector2> corners = [new(left, top), new(left, top + thickness), new(right, top + thickness), new(right, top)];
                        Vector2 corner = Vector2.Transform(corners[0], frame);
                        builder.MoveTo(corner.X, corner.Y);
                        for (int i = 1; i < 4; i++)
                        {
                            corner = Vector2.Transform(corners[i], frame);
                            builder.LineTo(corner.X, corner.Y);
                        }
                        builder.Close();
                    }
                }
            }
            start = end;

            // A span that would put its bars where this one's are: the same size on the same baseline.
            bool SameLine(VectorTextSpan other, Metrics otherMetrics) =>
                other.Font.Size == size && MathF.Abs(Down(other.Baseline, other.BaselineShift, other.ScriptRise, otherMetrics, other.Font.Size) - down) < 1e-3f;
        }

        bool Under(Cluster cluster)
        {
            if (cluster.Hidden || spans[cluster.Span].Hidden) return false;
            foreach (VectorTextLine one in spans[cluster.Span].Lines.IsDefault ? [] : spans[cluster.Span].Lines)
            {
                if (ReferenceEquals(one, line)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The stretches of x over which an outline, placed by <paramref name="place"/>, runs inside the band between two
    /// heights: for every piece of the outline that enters the band, from the leftmost to the rightmost point it has in it.
    /// </summary>
    private static void Crossings(VectorPath outline, Matrix3x2 place, float top, float bottom, List<(float From, float To)> found)
    {
        ReadOnlySpan<float> c = outline.Coordinates;
        Vector2 current = default, start = default;
        int i = 0;
        foreach (PathVerb verb in outline.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    current = start = P(c, i);
                    break;
                case PathVerb.Line:
                {
                    Vector2 to = P(c, i);
                    Segment(current, to);
                    current = to;
                    break;
                }
                case PathVerb.Quad:
                {
                    Vector2 q = P(c, i), to = P(c, i + 2), from = current;
                    for (int step = 1; step <= 8; step++)
                    {
                        float t = step / 8f, u = 1 - t;
                        Vector2 next = from * (u * u) + q * (2 * u * t) + to * (t * t);
                        Segment(current, next);
                        current = next;
                    }
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 q1 = P(c, i), q2 = P(c, i + 2), to = P(c, i + 4), from = current;
                    for (int step = 1; step <= 8; step++)
                    {
                        float t = step / 8f, u = 1 - t;
                        Vector2 next = from * (u * u * u) + q1 * (3 * u * u * t) + q2 * (3 * u * t * t) + to * (t * t * t);
                        Segment(current, next);
                        current = next;
                    }
                    break;
                }
                default:
                    Segment(current, start);
                    current = start;
                    break;
            }
            i += VectorPath.CoordinateCount(verb);
        }

        Vector2 P(ReadOnlySpan<float> coordinates, int at) => Vector2.Transform(new Vector2(coordinates[at], coordinates[at + 1]), place);

        void Segment(Vector2 a, Vector2 b)
        {
            if (MathF.Max(a.Y, b.Y) < top || MathF.Min(a.Y, b.Y) > bottom) return;
            // The part of the segment between the two heights.
            float t0 = 0, t1 = 1;
            if (a.Y != b.Y)
            {
                float atTop = (top - a.Y) / (b.Y - a.Y), atBottom = (bottom - a.Y) / (b.Y - a.Y);
                t0 = Math.Clamp(MathF.Min(atTop, atBottom), 0, 1);
                t1 = Math.Clamp(MathF.Max(atTop, atBottom), 0, 1);
            }
            float x0 = a.X + (b.X - a.X) * t0, x1 = a.X + (b.X - a.X) * t1;
            found.Add((MathF.Min(x0, x1), MathF.Max(x0, x1)));
        }
    }

    /// <summary>A glyph's outline in design units, y up, kept once it has been asked for.</summary>
    private static VectorPath OutlineOf(OpenTypeFont font, uint glyph, Dictionary<(OpenTypeFont, uint), VectorPath> known)
    {
        if (!known.TryGetValue((font, glyph), out VectorPath? outline)) known[(font, glyph)] = outline = OutlineOf(font, glyph);
        return outline;
    }

    /// <summary>A glyph's outline in design units, y up, or the empty path when the face has none for it.</summary>
    private static VectorPath OutlineOf(OpenTypeFont font, uint glyph)
    {
        if (!font.HasOutlines || glyph >= font.NumGlyphs) return VectorPath.Empty;
        var builder = new PathBuilder();
        try
        {
            font.DrawGlyph(glyph, new Collected(builder));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            // A glyph the face cannot give an outline for is a gap in the text, not the end of the picture.
            return VectorPath.Empty;
        }
        return builder.ToPath();
    }

    private sealed class Collected(PathBuilder builder) : IGlyphSink
    {
        public void MoveTo(float x, float y) => builder.MoveTo(x, y);
        public void LineTo(float x, float y) => builder.LineTo(x, y);
        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(cx, cy, x, y);
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => builder.CubicTo(c1x, c1y, c2x, c2y, x, y);
        public void Close() => builder.Close();
    }

    /// <summary>A path sink that maps every point: an affine map of a Bezier curve is the curve of the mapped points.</summary>
    private sealed class Placed(PathBuilder builder, Matrix3x2 map) : IPathSink
    {
        private Vector2 P(float x, float y) => Vector2.Transform(new Vector2(x, y), map);

        public void MoveTo(float x, float y)
        {
            Vector2 p = P(x, y);
            builder.MoveTo(p.X, p.Y);
        }

        public void LineTo(float x, float y)
        {
            Vector2 p = P(x, y);
            builder.LineTo(p.X, p.Y);
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Vector2 c = P(cx, cy), p = P(x, y);
            builder.QuadTo(c.X, c.Y, p.X, p.Y);
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Vector2 c1 = P(c1x, c1y), c2 = P(c2x, c2y), p = P(x, y);
            builder.CubicTo(c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y);
        }

        public void Close() => builder.Close();
    }

    // ---- Font metrics -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The lines of a face at a size: the ascent and descent of 'hhea' (of 'OS/2' when 'hhea' says nothing) and the
    /// height of an x, each with a fallback in ems when the face does not say.
    /// </summary>
    private static Metrics MetricsOf(OpenTypeFont font, float size)
    {
        float scale = size / Math.Max(1, (int)font.UnitsPerEm);
        float ascent = font.Hhea?.Ascender ?? 0, descent = -(font.Hhea?.Descender ?? 0);
        float xHeight = 0;
        if (font.TryGetTable(Tags.OS2, out ReadOnlyMemory<byte> os2))
        {
            ReadOnlySpan<byte> t = os2.Span;
            if (ascent == 0 && descent == 0 && t.Length >= 72)
            {
                ascent = BinaryPrimitives.ReadInt16BigEndian(t[68..]);
                descent = -BinaryPrimitives.ReadInt16BigEndian(t[70..]);
            }
            if (BinaryPrimitives.ReadUInt16BigEndian(t) >= 2 && t.Length >= 88) xHeight = BinaryPrimitives.ReadInt16BigEndian(t[86..]);
        }
        if (ascent == 0 && descent == 0) (ascent, descent) = (font.UnitsPerEm * 0.8f, font.UnitsPerEm * 0.2f);
        if (xHeight <= 0) xHeight = ascent * 0.56f;

        return new Metrics(ascent * scale, descent * scale, xHeight * scale);
    }

    // ---- A path to set text along -----------------------------------------------------------------------------------

    /// <summary>A path as a line of points with the distance to each, in the text's units.</summary>
    private sealed class Flattened(List<Vector2> points, List<float> distances)
    {
        public float Length => distances.Count == 0 ? 0 : distances[^1];

        public Vector2 End => points.Count == 0 ? default : points[^1];

        /// <summary>The point at a distance along the path and the direction there. Before the start and past the end the first and last pieces go on straight.</summary>
        public void At(float distance, out Vector2 point, out Vector2 tangent)
        {
            int i = distances.BinarySearch(distance);
            if (i < 0) i = ~i;
            i = Math.Clamp(i, 1, points.Count - 1);
            // Pieces of no length have no direction: take the nearest that has one.
            while (i < points.Count - 1 && points[i] == points[i - 1]) i++;
            Vector2 from = points[i - 1], to = points[i];
            tangent = to == from ? Vector2.UnitX : Vector2.Normalize(to - from);
            point = from + tangent * (distance - distances[i - 1]);
        }
    }

    private static Flattened Flatten(VectorTextPath along, Dictionary<VectorTextPath, Flattened> known)
    {
        if (known.TryGetValue(along, out Flattened? flattened)) return flattened;

        var points = new List<Vector2>();
        ReadOnlySpan<float> c = along.Path.Coordinates;
        Vector2 current = default, start = default;
        int i = 0;
        Vector2 P(int at) => Vector2.Transform(new Vector2(along.Path.Coordinates[at], along.Path.Coordinates[at + 1]), along.Transform);

        foreach (PathVerb verb in along.Path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    current = start = P(i);
                    points.Add(current);
                    break;
                case PathVerb.Line:
                    current = P(i);
                    points.Add(current);
                    break;
                case PathVerb.Quad:
                {
                    Vector2 q = P(i), to = P(i + 2);
                    for (int step = 1; step <= Pieces; step++)
                    {
                        float t = step / (float)Pieces, u = 1 - t;
                        points.Add(current * (u * u) + q * (2 * u * t) + to * (t * t));
                    }
                    current = to;
                    break;
                }
                case PathVerb.Cubic:
                {
                    Vector2 q1 = P(i), q2 = P(i + 2), to = P(i + 4);
                    for (int step = 1; step <= Pieces; step++)
                    {
                        float t = step / (float)Pieces, u = 1 - t;
                        points.Add(current * (u * u * u) + q1 * (3 * u * u * t) + q2 * (3 * u * t * t) + to * (t * t * t));
                    }
                    current = to;
                    break;
                }
                default:
                    if (current != start) points.Add(start);
                    current = start;
                    break;
            }
            i += VectorPath.CoordinateCount(verb);
        }
        if (along.RightSide) points.Reverse();

        var distances = new List<float>(points.Count);
        float total = 0;
        for (int p = 0; p < points.Count; p++)
        {
            if (p > 0) total += Vector2.Distance(points[p - 1], points[p]);
            distances.Add(total);
        }
        return known[along] = new Flattened(points, distances);
    }

    /// <summary>Straight pieces a curve of a text path is cut into.</summary>
    private const int Pieces = 32;
}
