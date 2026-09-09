namespace ChronoTerm.Terminal;

public readonly record struct TextBounds(float Left, float Top, float Right, float Bottom, float EffectiveWidth, float EffectiveHeight);

/// <summary>Port of layout.py. Pulled into its own file for the same reason the
/// Python version has its own module: this math is needed in two places
/// (render padding, mouse-to-cell conversion) and a single copy keeps them
/// from silently drifting apart.</summary>
public static class LayoutMath
{
    /// <summary>Corner-radius clipping means content near the edge gets cut off
    /// by the rounded shader mask — this is the margin needed to keep text
    /// clear of that curve. Zero when corner_radius is zero (the common case).</summary>
    public static (float top, float bottom, float left, float right) CornerRadiusCompensation(float cornerRadius)
    {
        if (cornerRadius <= 0) return (0, 0, 0, 0);
        float margin = cornerRadius + cornerRadius * 0.3f; // matches Python's corner_radius * 1.3 exactly
        return (margin, margin, margin, margin);
    }

    public static TextBounds EffectiveTextBounds(
        int windowWidth, int windowHeight,
        int paddingTop, int paddingBottom, int paddingLeft, int paddingRight,
        float cornerRadius)
    {
        var (cTop, cBottom, cLeft, cRight) = CornerRadiusCompensation(cornerRadius);

        float finalTop = cTop + paddingTop;
        float finalBottom = cBottom + paddingBottom;
        float finalLeft = cLeft + paddingLeft;
        float finalRight = cRight + paddingRight;

        float effWidth = Math.Max(100, windowWidth - finalLeft - finalRight);
        float effHeight = Math.Max(50, windowHeight - finalTop - finalBottom);

        return new TextBounds(finalLeft, finalTop, finalRight, finalBottom, effWidth, effHeight);
    }
}
