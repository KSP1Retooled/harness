namespace KspHarness.Approvals;

/// <summary>An 8-bit RGBA image, rows top to bottom.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} RGBA needs {width * height * 4} bytes, got {pixels.Length}");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public static RgbaImage FromPng(byte[] png) => Png.Decode(png);

    public byte[] ToPng() => Png.Encode(this);

    public int Offset(int x, int y) => (y * Width + x) * 4;

    /// <summary>
    /// Resamples to another size by area averaging: each target pixel is the coverage-weighted mean of the source
    /// pixels under it, which neither aliases when shrinking nor invents detail when growing.
    /// </summary>
    public RgbaImage Resize(int width, int height)
    {
        if (width == Width && height == Height)
        {
            return this;
        }

        var rows = new float[Height * width * 4];
        var xWeights = Weights(Width, width);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                foreach (var (source, weight) in xWeights[x])
                {
                    var from = Offset(source, y);
                    var to = (y * width + x) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        rows[to + c] += Pixels[from + c] * weight;
                    }
                }
            }
        }

        var result = new RgbaImage(width, height);
        var yWeights = Weights(Height, height);
        Span<float> sum = stackalloc float[4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                foreach (var (source, weight) in yWeights[y])
                {
                    var from = (source * width + x) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        sum[c] += rows[from + c] * weight;
                    }
                }

                var to = result.Offset(x, y);
                for (var c = 0; c < 4; c++)
                {
                    result.Pixels[to + c] = (byte)Math.Clamp(MathF.Round(sum[c]), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>For each target index, the source indices it covers and their share of it (summing to one).</summary>
    private static List<(int Source, float Weight)>[] Weights(int sourceSize, int targetSize)
    {
        var scale = (double)sourceSize / targetSize;
        var weights = new List<(int, float)>[targetSize];
        for (var t = 0; t < targetSize; t++)
        {
            var start = t * scale;
            var end = (t + 1) * scale;
            var list = new List<(int, float)>();
            for (var s = (int)Math.Floor(start); s < Math.Min(sourceSize, (int)Math.Ceiling(end)); s++)
            {
                var overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                if (overlap > 0)
                {
                    list.Add((s, (float)(overlap / scale)));
                }
            }

            weights[t] = list;
        }

        return weights;
    }
}
