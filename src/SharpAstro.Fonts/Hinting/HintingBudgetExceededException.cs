namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// Thrown internally when a hint program exceeds its instruction budget — in practice, when it
/// contains a loop that never satisfies its own exit condition.
/// </summary>
/// <remarks>
/// This never escapes the library: <see cref="HintingPipeline.Run"/> catches it and returns null,
/// so the caller transparently gets the unhinted outline instead. That degradation is deliberate.
/// Hinting is a refinement of a shape the font already defines, so dropping it costs a little
/// crispness at small sizes and nothing else, whereas the alternatives are both unacceptable for a
/// library whose main job is rendering fonts embedded in arbitrary PDFs: spinning forever wedges
/// the caller (in a browser, the whole tab), and throwing turns one malformed glyph into a failed
/// page render.
/// </remarks>
internal sealed class HintingBudgetExceededException : Exception
{
    public HintingBudgetExceededException()
        : base("Hint program exceeded its instruction budget.") { }
}
