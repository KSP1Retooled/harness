namespace KspHarness.Approvals;

/// <summary>The outcome of comparing a received image with an approved one.</summary>
public sealed class ImageComparison
{
    private ImageComparison(int width, int height, int compared, int different, int maxDelta, RgbaImage diff,
        bool rescaled, ScreenshotTolerance tolerance)
    {
        Width = width;
        Height = height;
        ComparedPixels = compared;
        DifferentPixels = different;
        MaxChannelDelta = maxDelta;
        Diff = diff;
        Rescaled = rescaled;
        Tolerance = tolerance;
    }

    /// <summary>The size the images were compared at.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Pixels outside the masks.</summary>
    public int ComparedPixels { get; }

    /// <summary>Compared pixels with a channel that differs by more than the tolerance.</summary>
    public int DifferentPixels { get; }

    public double DifferentFraction => ComparedPixels == 0 ? 0 : (double)DifferentPixels / ComparedPixels;

    /// <summary>The largest difference of any red, green or blue value among compared pixels.</summary>
    public int MaxChannelDelta { get; }

    /// <summary>
    /// The approved image dimmed to grey, with differing pixels in red, pixels that differ within the tolerance in
    /// yellow, and masked areas tinted blue.
    /// </summary>
    public RgbaImage Diff { get; }

    /// <summary>Whether the images differed in size and the larger was shrunk to the smaller one's size first.</summary>
    public bool Rescaled { get; }

    public ScreenshotTolerance Tolerance { get; }

    public bool Matches => DifferentFraction <= Tolerance.MaxDifferentFraction;

    public string Describe()
    {
        return $"{DifferentPixels} of {ComparedPixels} pixels ({DifferentFraction:P3}) differ by more than "
            + $"{Tolerance.ChannelTolerance} (largest difference {MaxChannelDelta}, allowed {Tolerance.MaxDifferentFraction:P3}"
            + $"{(Rescaled ? "; the larger image was shrunk to the smaller one's size" : "")}"
            + $"{(Tolerance.Downscale > 1 ? $"; compared at {Width}x{Height}" : "")})";
    }

    /// <summary>
    /// Compares <paramref name="received"/> with <paramref name="approved"/>. A received image of another size with the
    /// same aspect ratio (within 1%) is compared after shrinking the larger image to the smaller one's size; another
    /// aspect ratio throws <see cref="IncomparableImagesException"/>.
    /// </summary>
    public static ImageComparison Compare(RgbaImage approved, RgbaImage received, ScreenshotTolerance? tolerance = null)
    {
        tolerance ??= ScreenshotTolerance.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance.Downscale, 1);

        var rescaled = false;
        if (received.Width != approved.Width || received.Height != approved.Height)
        {
            var approvedAspect = (double)approved.Width / approved.Height;
            var receivedAspect = (double)received.Width / received.Height;
            if (Math.Abs(receivedAspect / approvedAspect - 1) > 0.01)
            {
                throw new IncomparableImagesException(
                    $"received image is {received.Width}x{received.Height} and approved is "
                    + $"{approved.Width}x{approved.Height}: the aspect ratios differ, so they cannot be compared");
            }

            // Shrink the larger one: enlarging cannot restore detail, so it would fail on every sharp edge.
            if (received.Width > approved.Width)
            {
                received = received.Resize(approved.Width, approved.Height);
            }
            else
            {
                approved = approved.Resize(received.Width, received.Height);
            }

            rescaled = true;
        }

        if (tolerance.Downscale > 1)
        {
            var width = Math.Max(1, approved.Width / tolerance.Downscale);
            var height = Math.Max(1, approved.Height / tolerance.Downscale);
            approved = approved.Resize(width, height);
            received = received.Resize(width, height);
        }

        var diff = new RgbaImage(approved.Width, approved.Height);
        int compared = 0, different = 0, maxDelta = 0;
        for (var y = 0; y < approved.Height; y++)
        {
            var v = (y + 0.5) / approved.Height;
            for (var x = 0; x < approved.Width; x++)
            {
                var at = approved.Offset(x, y);
                var gray = (approved.Pixels[at] * 77 + approved.Pixels[at + 1] * 150 + approved.Pixels[at + 2] * 29) >> 8;
                var dim = (byte)(gray / 3);

                var u = (x + 0.5) / approved.Width;
                if (tolerance.Masks.Any(mask => mask.Contains(u, v)))
                {
                    Set(diff, at, dim, dim, (byte)Math.Min(255, dim + 96));
                    continue;
                }

                compared++;
                var delta = 0;
                for (var c = 0; c < 3; c++)
                {
                    delta = Math.Max(delta, Math.Abs(approved.Pixels[at + c] - received.Pixels[at + c]));
                }

                maxDelta = Math.Max(maxDelta, delta);
                if (delta > tolerance.ChannelTolerance)
                {
                    different++;
                    Set(diff, at, 255, 0, 0);
                }
                else if (delta > 0)
                {
                    Set(diff, at, 160, 160, 0);
                }
                else
                {
                    Set(diff, at, dim, dim, dim);
                }
            }
        }

        return new ImageComparison(approved.Width, approved.Height, compared, different, maxDelta, diff, rescaled, tolerance);
    }

    private static void Set(RgbaImage image, int at, byte r, byte g, byte b)
    {
        image.Pixels[at] = r;
        image.Pixels[at + 1] = g;
        image.Pixels[at + 2] = b;
        image.Pixels[at + 3] = 255;
    }
}

/// <summary>The images have different aspect ratios, so no tolerance can make them match.</summary>
public sealed class IncomparableImagesException(string message) : ArgumentException(message);
