global using Shouldly;
global using SharpAstro.Fonts;
global using SharpAstro.Fonts.Color;
global using Xunit;
// The colour type is the scene's since Fonts.Lib 2.0 (SharpAstro.Fonts.Color.Rgba32 is gone). Many tests here also
// import SharpAstro.Fonts.Tables.Colr, whose GradientExtend would clash with the scene's, hence an alias and not the namespace.
global using Rgba32 = SharpAstro.VectorGraphics.Rgba32;
