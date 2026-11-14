namespace KspHarness.Approvals;

/// <summary>
/// A rectangle in normalised image coordinates: 0 to 1 across and down from the top-left corner, so that it covers the
/// same part of the screen at any resolution.
/// </summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y)
    {
        return x >= X && x < X + Width && y >= Y && y < Y + Height;
    }
}

/// <summary>How different a screenshot may be from its approved image and still match.</summary>
public sealed record ScreenshotTolerance
{
    /// <summary>
    /// Allows the small noise of the same frame rendered twice (dithering, filtering), and nothing a person would see.
    /// </summary>
    public static ScreenshotTolerance Default { get; } = new();

    /// <summary>
    /// A pixel differs when any of its red, green or blue values differs by more than this (0-255). Alpha is not
    /// compared, because a screenshot's alpha is whatever the shaders happened to write.
    /// </summary>
    public int ChannelTolerance { get; init; } = 8;

    /// <summary>The share of compared pixels (0-1) that may differ.</summary>
    public double MaxDifferentFraction { get; init; } = 0.001;

    /// <summary>Areas that are not compared, such as animated or time-dependent parts of the screen.</summary>
    public IReadOnlyList<NormalizedRect> Masks { get; init; } = [];

    /// <summary>
    /// Shrinks both images by this factor (area averaging) before comparing, which absorbs differences of a pixel or
    /// two in where edges fall. 1 compares at full size.
    /// </summary>
    public int Downscale { get; init; } = 1;
}
