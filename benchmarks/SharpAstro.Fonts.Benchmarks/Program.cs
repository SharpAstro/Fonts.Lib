using BenchmarkDotNet.Running;
using SharpAstro.Fonts;
using SharpAstro.Fonts.Benchmarks;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping;

// Diagnostic: `dotnet run -- --lookups` prints how many GSUB/GPOS lookups the DejaVu `latn` plan
// enables — i.e. how many coverage probes per glyph the LookupRunner would do without the digest.
if (args.Length > 0 && args[0] == "--lookups")
{
    var dejaVu = ShapingFont.Create(OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans)));
    var plan = dejaVu.GetPlan(new Tag("latn"), ShapeDirection.LeftToRight);
    Console.WriteLine($"DejaVu Sans 'latn' plan: {plan.SubstitutionLookups.Length} GSUB + " +
        $"{plan.PositioningLookups.Length} GPOS lookups = " +
        $"{plan.SubstitutionLookups.Length + plan.PositioningLookups.Length} coverage probes/glyph (pre-digest)");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
