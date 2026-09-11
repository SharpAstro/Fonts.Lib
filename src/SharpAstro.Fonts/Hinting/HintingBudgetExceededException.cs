namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// Thrown internally when a hint program has to be abandoned: it exceeded its instruction budget —
/// in practice, a loop that never satisfies its own exit condition — or it nested CALL past the
/// interpreter's depth cap, which is a function that calls itself.
/// </summary>
/// <remarks>
/// This never escapes the library: <see cref="HintingPipeline.Run"/> catches it and returns null,
/// so the caller transparently gets the unhinted outline instead. That degradation is deliberate.
/// Hinting is a refinement of a shape the font already defines, so dropping it costs a little
/// crispness at small sizes and nothing else, whereas the alternatives are both unacceptable for a
/// library whose main job is rendering fonts embedded in arbitrary PDFs: spinning forever wedges
/// the caller (in a browser, the whole tab), and throwing turns one malformed glyph into a failed
/// page render.
/// <para>The call-depth case is the more serious of the two, and is why the cap exists rather than
/// being left to the instruction budget: a runaway CALL chain recurses the interpreter's own
/// <c>Execute</c>, so it exhausts the MACHINE stack. A .NET stack overflow cannot be caught — the
/// process dies with no exception — so no amount of defensive catching in a caller helps. The bound
/// has to be inside the interpreter.</para>
/// </remarks>
internal sealed class HintingBudgetExceededException : Exception
{
    public HintingBudgetExceededException()
        : base("Hint program exceeded its instruction budget.") { }

    public HintingBudgetExceededException(string message) : base(message) { }
}
