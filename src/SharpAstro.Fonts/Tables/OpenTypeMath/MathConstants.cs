using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// Global math typesetting constants for a font, in font design units (FUnits).
/// Convert to pixels with the standard <c>FUnits × pointSize × 72 / unitsPerEm</c>
/// (or <c>FUnits / unitsPerEm × pointSize</c> in points).
///
/// <para>All <c>MathValueRecord</c> entries in the spec carry an optional device
/// table for pixel-level snapping; this implementation discards the device
/// offset and keeps only the FUnit value, since consumers rasterising at
/// non-integer ppem don't need the snap and integer-ppem snapping is rarely
/// worth the complexity for math metrics.</para>
///
/// <para>Spec: <see href="https://learn.microsoft.com/en-us/typography/opentype/spec/math#mathconstants-table"/></para>
/// </summary>
public sealed class MathConstants
{
    /// <summary>Percentage scale-down for sub/super-scripts (e.g. 80 = 80%).</summary>
    public short ScriptPercentScaleDown { get; }
    /// <summary>Percentage scale-down for second-level sub/super-scripts.</summary>
    public short ScriptScriptPercentScaleDown { get; }
    /// <summary>Minimum height (FUnits) of a delimited sub-formula before it
    /// gets surrounded by stretchy delimiters at display size.</summary>
    public ushort DelimitedSubFormulaMinHeight { get; }
    /// <summary>Minimum height (FUnits) of an operator displayed in display
    /// style with limits stacked above/below — i.e. the minimum size at which
    /// a big operator like ∫ ∑ stretches up.</summary>
    public ushort DisplayOperatorMinHeight { get; }

    public short MathLeading { get; }
    /// <summary>Distance (FUnits, positive up) from the baseline to the math
    /// axis — the horizontal level on which fraction bars sit and ± / = / − /
    /// big-operator centres rest. The single most-used metric for layout.</summary>
    public short AxisHeight { get; }
    public short AccentBaseHeight { get; }
    public short FlattenedAccentBaseHeight { get; }

    public short SubscriptShiftDown { get; }
    public short SubscriptTopMax { get; }
    public short SubscriptBaselineDropMin { get; }

    public short SuperscriptShiftUp { get; }
    public short SuperscriptShiftUpCramped { get; }
    public short SuperscriptBottomMin { get; }
    public short SuperscriptBaselineDropMax { get; }

    public short SubSuperscriptGapMin { get; }
    public short SuperscriptBottomMaxWithSubscript { get; }
    public short SpaceAfterScript { get; }

    public short UpperLimitGapMin { get; }
    public short UpperLimitBaselineRiseMin { get; }
    public short LowerLimitGapMin { get; }
    public short LowerLimitBaselineDropMin { get; }

    public short StackTopShiftUp { get; }
    public short StackTopDisplayStyleShiftUp { get; }
    public short StackBottomShiftDown { get; }
    public short StackBottomDisplayStyleShiftDown { get; }
    public short StackGapMin { get; }
    public short StackDisplayStyleGapMin { get; }

    public short StretchStackTopShiftUp { get; }
    public short StretchStackBottomShiftDown { get; }
    public short StretchStackGapAboveMin { get; }
    public short StretchStackGapBelowMin { get; }

    public short FractionNumeratorShiftUp { get; }
    public short FractionNumeratorDisplayStyleShiftUp { get; }
    public short FractionDenominatorShiftDown { get; }
    public short FractionDenominatorDisplayStyleShiftDown { get; }
    public short FractionNumeratorGapMin { get; }
    public short FractionNumeratorDisplayStyleGapMin { get; }
    /// <summary>Default thickness (FUnits) of the fraction rule. Matches the
    /// glyph stem thickness in well-designed math fonts.</summary>
    public short FractionRuleThickness { get; }
    public short FractionDenominatorGapMin { get; }
    public short FractionDenominatorDisplayStyleGapMin { get; }

    public short SkewedFractionHorizontalGap { get; }
    public short SkewedFractionVerticalGap { get; }

    public short OverbarVerticalGap { get; }
    public short OverbarRuleThickness { get; }
    public short OverbarExtraAscender { get; }

    public short UnderbarVerticalGap { get; }
    public short UnderbarRuleThickness { get; }
    public short UnderbarExtraDescender { get; }

    /// <summary>Minimum gap (FUnits) between the radical's vinculum and the
    /// content underneath it, when not in display style.</summary>
    public short RadicalVerticalGap { get; }
    /// <summary>As above, but for display style — typically larger, giving
    /// the radical content more breathing room.</summary>
    public short RadicalDisplayStyleVerticalGap { get; }
    /// <summary>Default thickness (FUnits) of the radical's vinculum.</summary>
    public short RadicalRuleThickness { get; }
    public short RadicalExtraAscender { get; }
    public short RadicalKernBeforeDegree { get; }
    public short RadicalKernAfterDegree { get; }
    /// <summary>Percentage of the radical's height by which the degree (the
    /// "n" in ⁿ√x) is raised above the bottom of the radical.</summary>
    public short RadicalDegreeBottomRaisePercent { get; }

    private MathConstants(
        short scriptPercentScaleDown, short scriptScriptPercentScaleDown,
        ushort delimitedSubFormulaMinHeight, ushort displayOperatorMinHeight,
        short mathLeading, short axisHeight, short accentBaseHeight, short flattenedAccentBaseHeight,
        short subscriptShiftDown, short subscriptTopMax, short subscriptBaselineDropMin,
        short superscriptShiftUp, short superscriptShiftUpCramped, short superscriptBottomMin, short superscriptBaselineDropMax,
        short subSuperscriptGapMin, short superscriptBottomMaxWithSubscript, short spaceAfterScript,
        short upperLimitGapMin, short upperLimitBaselineRiseMin, short lowerLimitGapMin, short lowerLimitBaselineDropMin,
        short stackTopShiftUp, short stackTopDisplayStyleShiftUp, short stackBottomShiftDown, short stackBottomDisplayStyleShiftDown,
        short stackGapMin, short stackDisplayStyleGapMin,
        short stretchStackTopShiftUp, short stretchStackBottomShiftDown, short stretchStackGapAboveMin, short stretchStackGapBelowMin,
        short fractionNumeratorShiftUp, short fractionNumeratorDisplayStyleShiftUp,
        short fractionDenominatorShiftDown, short fractionDenominatorDisplayStyleShiftDown,
        short fractionNumeratorGapMin, short fractionNumeratorDisplayStyleGapMin,
        short fractionRuleThickness,
        short fractionDenominatorGapMin, short fractionDenominatorDisplayStyleGapMin,
        short skewedFractionHorizontalGap, short skewedFractionVerticalGap,
        short overbarVerticalGap, short overbarRuleThickness, short overbarExtraAscender,
        short underbarVerticalGap, short underbarRuleThickness, short underbarExtraDescender,
        short radicalVerticalGap, short radicalDisplayStyleVerticalGap, short radicalRuleThickness, short radicalExtraAscender,
        short radicalKernBeforeDegree, short radicalKernAfterDegree, short radicalDegreeBottomRaisePercent)
    {
        ScriptPercentScaleDown = scriptPercentScaleDown;
        ScriptScriptPercentScaleDown = scriptScriptPercentScaleDown;
        DelimitedSubFormulaMinHeight = delimitedSubFormulaMinHeight;
        DisplayOperatorMinHeight = displayOperatorMinHeight;
        MathLeading = mathLeading;
        AxisHeight = axisHeight;
        AccentBaseHeight = accentBaseHeight;
        FlattenedAccentBaseHeight = flattenedAccentBaseHeight;
        SubscriptShiftDown = subscriptShiftDown;
        SubscriptTopMax = subscriptTopMax;
        SubscriptBaselineDropMin = subscriptBaselineDropMin;
        SuperscriptShiftUp = superscriptShiftUp;
        SuperscriptShiftUpCramped = superscriptShiftUpCramped;
        SuperscriptBottomMin = superscriptBottomMin;
        SuperscriptBaselineDropMax = superscriptBaselineDropMax;
        SubSuperscriptGapMin = subSuperscriptGapMin;
        SuperscriptBottomMaxWithSubscript = superscriptBottomMaxWithSubscript;
        SpaceAfterScript = spaceAfterScript;
        UpperLimitGapMin = upperLimitGapMin;
        UpperLimitBaselineRiseMin = upperLimitBaselineRiseMin;
        LowerLimitGapMin = lowerLimitGapMin;
        LowerLimitBaselineDropMin = lowerLimitBaselineDropMin;
        StackTopShiftUp = stackTopShiftUp;
        StackTopDisplayStyleShiftUp = stackTopDisplayStyleShiftUp;
        StackBottomShiftDown = stackBottomShiftDown;
        StackBottomDisplayStyleShiftDown = stackBottomDisplayStyleShiftDown;
        StackGapMin = stackGapMin;
        StackDisplayStyleGapMin = stackDisplayStyleGapMin;
        StretchStackTopShiftUp = stretchStackTopShiftUp;
        StretchStackBottomShiftDown = stretchStackBottomShiftDown;
        StretchStackGapAboveMin = stretchStackGapAboveMin;
        StretchStackGapBelowMin = stretchStackGapBelowMin;
        FractionNumeratorShiftUp = fractionNumeratorShiftUp;
        FractionNumeratorDisplayStyleShiftUp = fractionNumeratorDisplayStyleShiftUp;
        FractionDenominatorShiftDown = fractionDenominatorShiftDown;
        FractionDenominatorDisplayStyleShiftDown = fractionDenominatorDisplayStyleShiftDown;
        FractionNumeratorGapMin = fractionNumeratorGapMin;
        FractionNumeratorDisplayStyleGapMin = fractionNumeratorDisplayStyleGapMin;
        FractionRuleThickness = fractionRuleThickness;
        FractionDenominatorGapMin = fractionDenominatorGapMin;
        FractionDenominatorDisplayStyleGapMin = fractionDenominatorDisplayStyleGapMin;
        SkewedFractionHorizontalGap = skewedFractionHorizontalGap;
        SkewedFractionVerticalGap = skewedFractionVerticalGap;
        OverbarVerticalGap = overbarVerticalGap;
        OverbarRuleThickness = overbarRuleThickness;
        OverbarExtraAscender = overbarExtraAscender;
        UnderbarVerticalGap = underbarVerticalGap;
        UnderbarRuleThickness = underbarRuleThickness;
        UnderbarExtraDescender = underbarExtraDescender;
        RadicalVerticalGap = radicalVerticalGap;
        RadicalDisplayStyleVerticalGap = radicalDisplayStyleVerticalGap;
        RadicalRuleThickness = radicalRuleThickness;
        RadicalExtraAscender = radicalExtraAscender;
        RadicalKernBeforeDegree = radicalKernBeforeDegree;
        RadicalKernAfterDegree = radicalKernAfterDegree;
        RadicalDegreeBottomRaisePercent = radicalDegreeBottomRaisePercent;
    }

    /// <summary>
    /// Read a MathValueRecord (int16 value + Offset16 device-table). The
    /// device-table offset is discarded; consumers don't pixel-snap math
    /// metrics in this implementation.
    /// </summary>
    private static short ReadValueRecord(ref BigEndianReader r)
    {
        var v = r.ReadInt16();
        r.Skip(2); // device table offset
        return v;
    }

    internal static MathConstants Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        return new MathConstants(
            scriptPercentScaleDown: r.ReadInt16(),
            scriptScriptPercentScaleDown: r.ReadInt16(),
            delimitedSubFormulaMinHeight: r.ReadUInt16(),
            displayOperatorMinHeight: r.ReadUInt16(),
            mathLeading: ReadValueRecord(ref r),
            axisHeight: ReadValueRecord(ref r),
            accentBaseHeight: ReadValueRecord(ref r),
            flattenedAccentBaseHeight: ReadValueRecord(ref r),
            subscriptShiftDown: ReadValueRecord(ref r),
            subscriptTopMax: ReadValueRecord(ref r),
            subscriptBaselineDropMin: ReadValueRecord(ref r),
            superscriptShiftUp: ReadValueRecord(ref r),
            superscriptShiftUpCramped: ReadValueRecord(ref r),
            superscriptBottomMin: ReadValueRecord(ref r),
            superscriptBaselineDropMax: ReadValueRecord(ref r),
            subSuperscriptGapMin: ReadValueRecord(ref r),
            superscriptBottomMaxWithSubscript: ReadValueRecord(ref r),
            spaceAfterScript: ReadValueRecord(ref r),
            upperLimitGapMin: ReadValueRecord(ref r),
            upperLimitBaselineRiseMin: ReadValueRecord(ref r),
            lowerLimitGapMin: ReadValueRecord(ref r),
            lowerLimitBaselineDropMin: ReadValueRecord(ref r),
            stackTopShiftUp: ReadValueRecord(ref r),
            stackTopDisplayStyleShiftUp: ReadValueRecord(ref r),
            stackBottomShiftDown: ReadValueRecord(ref r),
            stackBottomDisplayStyleShiftDown: ReadValueRecord(ref r),
            stackGapMin: ReadValueRecord(ref r),
            stackDisplayStyleGapMin: ReadValueRecord(ref r),
            stretchStackTopShiftUp: ReadValueRecord(ref r),
            stretchStackBottomShiftDown: ReadValueRecord(ref r),
            stretchStackGapAboveMin: ReadValueRecord(ref r),
            stretchStackGapBelowMin: ReadValueRecord(ref r),
            fractionNumeratorShiftUp: ReadValueRecord(ref r),
            fractionNumeratorDisplayStyleShiftUp: ReadValueRecord(ref r),
            fractionDenominatorShiftDown: ReadValueRecord(ref r),
            fractionDenominatorDisplayStyleShiftDown: ReadValueRecord(ref r),
            fractionNumeratorGapMin: ReadValueRecord(ref r),
            fractionNumeratorDisplayStyleGapMin: ReadValueRecord(ref r),
            fractionRuleThickness: ReadValueRecord(ref r),
            fractionDenominatorGapMin: ReadValueRecord(ref r),
            fractionDenominatorDisplayStyleGapMin: ReadValueRecord(ref r),
            skewedFractionHorizontalGap: ReadValueRecord(ref r),
            skewedFractionVerticalGap: ReadValueRecord(ref r),
            overbarVerticalGap: ReadValueRecord(ref r),
            overbarRuleThickness: ReadValueRecord(ref r),
            overbarExtraAscender: ReadValueRecord(ref r),
            underbarVerticalGap: ReadValueRecord(ref r),
            underbarRuleThickness: ReadValueRecord(ref r),
            underbarExtraDescender: ReadValueRecord(ref r),
            radicalVerticalGap: ReadValueRecord(ref r),
            radicalDisplayStyleVerticalGap: ReadValueRecord(ref r),
            radicalRuleThickness: ReadValueRecord(ref r),
            radicalExtraAscender: ReadValueRecord(ref r),
            radicalKernBeforeDegree: ReadValueRecord(ref r),
            radicalKernAfterDegree: ReadValueRecord(ref r),
            radicalDegreeBottomRaisePercent: r.ReadInt16());
    }
}
