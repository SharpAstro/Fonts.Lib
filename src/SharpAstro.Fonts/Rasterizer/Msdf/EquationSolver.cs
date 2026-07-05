namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// Real-root solvers for the quadratic and cubic equations the closest-point
/// math needs. A faithful port of msdfgen's <c>equation-solver</c> (the
/// trigonometric / Cardano cubic), so the generated distances match the
/// reference generator. Returns the number of distinct real roots written into
/// the caller's span.
/// </summary>
internal static class EquationSolver
{
    private const double TooLargeRatio = 1e12;

    /// <summary>Solve a·x² + b·x + c = 0. Returns root count (−1 means "all reals", i.e. degenerate 0 = 0).</summary>
    public static int SolveQuadratic(Span<double> x, double a, double b, double c)
    {
        // Treat as linear (or degenerate) when the leading coefficient is negligible relative to the others.
        if (a == 0 || Math.Abs(b) + Math.Abs(c) > TooLargeRatio * Math.Abs(a))
        {
            if (b == 0 || Math.Abs(c) > TooLargeRatio * Math.Abs(b))
                return c == 0 ? -1 : 0;
            x[0] = -c / b;
            return 1;
        }

        var dscr = b * b - 4 * a * c;
        if (dscr > 0)
        {
            dscr = Math.Sqrt(dscr);
            x[0] = (-b + dscr) / (2 * a);
            x[1] = (-b - dscr) / (2 * a);
            return 2;
        }

        if (dscr == 0)
        {
            x[0] = -b / (2 * a);
            return 1;
        }

        return 0;
    }

    private static int SolveCubicNormed(Span<double> x, double a, double b, double c)
    {
        var a2 = a * a;
        var q = (a2 - 3 * b) / 9;
        var r = (a * (2 * a2 - 9 * b) + 27 * c) / 54;
        var r2 = r * r;
        var q3 = q * q * q;
        a /= 3;

        if (r2 < q3)
        {
            var t = r / Math.Sqrt(q3);
            t = Math.Clamp(t, -1, 1);
            t = Math.Acos(t);
            q = -2 * Math.Sqrt(q);
            x[0] = q * Math.Cos(t / 3) - a;
            x[1] = q * Math.Cos((t + 2 * Math.PI) / 3) - a;
            x[2] = q * Math.Cos((t - 2 * Math.PI) / 3) - a;
            return 3;
        }
        else
        {
            var u = (r < 0 ? 1 : -1) * Math.Pow(Math.Abs(r) + Math.Sqrt(r2 - q3), 1.0 / 3.0);
            var v = u == 0 ? 0 : q / u;
            x[0] = (u + v) - a;
            if (u == v || Math.Abs(u - v) < 1e-12 * Math.Abs(u + v))
            {
                x[1] = -0.5 * (u + v) - a;
                return 2;
            }

            return 1;
        }
    }

    /// <summary>Solve a·x³ + b·x² + c·x + d = 0. Returns root count.</summary>
    public static int SolveCubic(Span<double> x, double a, double b, double c, double d)
    {
        if (a != 0)
        {
            var bn = b / a;
            if (Math.Abs(bn) < TooLargeRatio)
                return SolveCubicNormed(x, bn, c / a, d / a);
        }

        return SolveQuadratic(x, b, c, d);
    }
}
