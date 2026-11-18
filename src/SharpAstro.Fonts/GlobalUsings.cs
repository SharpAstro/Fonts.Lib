// One colour type for the stack: the scene's, from SharpAstro.Vector. Fonts.Lib had its own of the same shape
// (SharpAstro.Fonts.Color.Rgba32) until 2.0. The alias keeps this assembly's files, most of which do not import
// SharpAstro.VectorGraphics (its GradientExtend would be ambiguous with the COLR one), reading as they did.
global using Rgba32 = SharpAstro.VectorGraphics.Rgba32;
