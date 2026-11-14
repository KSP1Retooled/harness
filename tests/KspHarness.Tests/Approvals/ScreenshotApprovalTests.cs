using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KspHarness.Approvals;

namespace KspHarness.Tests.Approvals;

public sealed class ScreenshotApprovalTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ksph-approvals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private ApprovalContext Context(bool? approve = false) => new(folder, "Suite", "Test") { Approve = approve };

    private string PathOf(string kind) => Path.Combine(folder, $"Suite.Test.menu.{kind}.png");

    /// <summary>A gradient with a few hard edges, so that resampling and noise have something to disturb.</summary>
    private static RgbaImage Scene(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = image.Offset(x, y);
                var u = (x + 0.5) / width;
                var v = (y + 0.5) / height;
                image.Pixels[at] = (byte)(u * 255);
                image.Pixels[at + 1] = (byte)(v * 255);
                image.Pixels[at + 2] = (byte)(u > 0.5 && v > 0.5 ? 200 : 40);
                image.Pixels[at + 3] = 255;
            }
        }

        return image;
    }

    private static void Paint(RgbaImage image, int x0, int y0, int width, int height, byte r, byte g, byte b)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                var at = image.Offset(x, y);
                image.Pixels[at] = r;
                image.Pixels[at + 1] = g;
                image.Pixels[at + 2] = b;
            }
        }
    }

    private void SaveApproved(RgbaImage image)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(PathOf("approved"), image.ToPng());
    }

    [Fact]
    public void Png_round_trips()
    {
        var image = Scene(37, 23);
        image.Pixels[image.Offset(3, 4) + 3] = 17;

        var decoded = RgbaImage.FromPng(image.ToPng());

        Assert.Equal(37, decoded.Width);
        Assert.Equal(23, decoded.Height);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(0, 8, false)]
    [InlineData(2, 8, false)]
    [InlineData(2, 16, false)]
    [InlineData(3, 4, false)]
    [InlineData(4, 8, false)]
    [InlineData(6, 8, true)]
    [InlineData(0, 1, true)]
    public void Pngs_of_other_formats_decode(int colorType, int bitDepth, bool interlaced)
    {
        // A 9x9 checkerboard of black and white, with a palette of [black, white] for colour type 3.
        var png = HandMadePng(colorType, bitDepth, interlaced, 9, 9, (x, y) => (x + y) % 2);

        var image = RgbaImage.FromPng(png);

        for (var y = 0; y < 9; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                var expected = (x + y) % 2 == 1 ? 255 : 0;
                var at = image.Offset(x, y);
                Assert.True(image.Pixels[at] == expected && image.Pixels[at + 2] == expected, $"pixel {x},{y}");
                Assert.Equal(255, image.Pixels[at + 3]);
            }
        }
    }

    [Fact]
    public void Noise_within_the_tolerance_matches()
    {
        var approved = Scene(120, 80);
        SaveApproved(approved);
        var received = Scene(120, 80);
        var random = new Random(1);
        for (var i = 0; i < received.Pixels.Length; i++)
        {
            received.Pixels[i] = (byte)Math.Clamp(received.Pixels[i] + random.Next(-4, 5), 0, 255);
        }

        // A few outliers fit in the allowed fraction.
        Paint(received, 0, 0, 3, 3, 255, 255, 255);

        var comparison = Approve.Screenshot(
            received.ToPng(),
            "menu",
            new ScreenshotTolerance { ChannelTolerance = 4, MaxDifferentFraction = 0.002 },
            Context()
        );

        Assert.NotNull(comparison);
        Assert.True(comparison.Matches);
        Assert.InRange(comparison.DifferentPixels, 1, 9);
        Assert.False(File.Exists(PathOf("received")));
    }

    [Fact]
    public void A_mask_hides_a_changed_region()
    {
        SaveApproved(Scene(200, 100));
        var received = Scene(200, 100);
        Paint(received, 150, 10, 40, 20, 255, 0, 255);
        var tolerance = new ScreenshotTolerance { MaxDifferentFraction = 0 };

        Assert.Throws<ApprovalException>(() => Approve.Screenshot(received.ToPng(), "menu", tolerance, Context()));

        var masked = tolerance with { Masks = [new NormalizedRect(0.74, 0.09, 0.22, 0.22)] };
        var comparison = Approve.Screenshot(received.ToPng(), "menu", masked, Context());

        Assert.NotNull(comparison);
        Assert.Equal(0, comparison.DifferentPixels);
        Assert.True(comparison.ComparedPixels < 200 * 100);
        Assert.False(File.Exists(PathOf("received")));
        Assert.False(File.Exists(PathOf("diff")));
    }

    [Fact]
    public void A_larger_image_of_the_same_aspect_is_rescaled()
    {
        SaveApproved(Scene(160, 90));

        var comparison = Approve.Screenshot(Scene(320, 180).ToPng(), "menu", null, Context());

        Assert.NotNull(comparison);
        Assert.True(comparison.Rescaled);
        Assert.Equal(160, comparison.Width);
        Assert.True(comparison.Matches, comparison.Describe());
    }

    [Fact]
    public void A_smaller_image_of_the_same_aspect_is_compared_at_its_own_size()
    {
        SaveApproved(Scene(320, 180));

        var comparison = Approve.Screenshot(Scene(160, 90).ToPng(), "menu", null, Context());

        Assert.NotNull(comparison);
        Assert.True(comparison.Rescaled);
        Assert.Equal(160, comparison.Width);
        Assert.True(comparison.Matches, comparison.Describe());
    }

    [Fact]
    public void Downscaling_absorbs_a_one_pixel_shift()
    {
        var approved = new RgbaImage(64, 64);
        Paint(approved, 20, 0, 1, 64, 255, 255, 255);
        var received = new RgbaImage(64, 64);
        Paint(received, 21, 0, 1, 64, 255, 255, 255);
        SaveApproved(approved);

        var strict = new ScreenshotTolerance { ChannelTolerance = 70, MaxDifferentFraction = 0 };
        Assert.Throws<ApprovalException>(() => Approve.Screenshot(received.ToPng(), "menu", strict, Context()));

        var comparison = Approve.Screenshot(received.ToPng(), "menu", strict with { Downscale = 4 }, Context());
        Assert.NotNull(comparison);
        Assert.Equal(16, comparison.Width);
    }

    [Fact]
    public void Another_aspect_ratio_fails_clearly()
    {
        SaveApproved(Scene(160, 90));

        var e = Assert.Throws<ApprovalException>(() =>
            Approve.Screenshot(Scene(160, 120).ToPng(), "menu", null, Context())
        );

        Assert.Contains("aspect ratios differ", e.Message);
        Assert.True(File.Exists(PathOf("received")));
    }

    [Fact]
    public void An_invalid_tolerance_is_a_programming_error_even_when_approving()
    {
        var approved = Scene(50, 50);
        SaveApproved(approved);
        var before = File.ReadAllBytes(PathOf("approved"));
        var received = Scene(50, 50);
        Paint(received, 0, 0, 25, 25, 0, 0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Approve.Screenshot(
                received.ToPng(),
                "menu",
                new ScreenshotTolerance { Downscale = 0 },
                Context(approve: true)
            )
        );

        Assert.Equal(before, File.ReadAllBytes(PathOf("approved")));
    }

    [Fact]
    public void A_failing_image_writes_received_and_diff_pngs()
    {
        SaveApproved(Scene(100, 100));
        var received = Scene(100, 100);
        Paint(received, 10, 60, 20, 20, 0, 0, 0);

        var e = Assert.Throws<ApprovalException>(() => Approve.Screenshot(received.ToPng(), "menu", null, Context()));

        Assert.Contains("400 of 10000 pixels", e.Message);
        Assert.Equal(PathOf("diff"), e.DiffPath);
        Assert.Equal(received.Pixels, RgbaImage.FromPng(File.ReadAllBytes(PathOf("received"))).Pixels);

        var diff = RgbaImage.FromPng(File.ReadAllBytes(PathOf("diff")));
        var inside = diff.Offset(15, 65);
        var outside = diff.Offset(80, 20);
        Assert.Equal([255, 0, 0], diff.Pixels[inside..(inside + 3)]);
        Assert.NotEqual(255, diff.Pixels[outside]);
    }

    [Fact]
    public void Approve_mode_replaces_the_approved_image()
    {
        SaveApproved(Scene(50, 50));
        var received = Scene(50, 50);
        Paint(received, 0, 0, 25, 25, 0, 0, 0);
        var png = received.ToPng();

        Approve.Screenshot(png, "menu", null, Context(approve: true));

        Assert.Equal(png, File.ReadAllBytes(PathOf("approved")));
        Assert.True(Approve.Screenshot(png, "menu", null, Context())!.Matches);
    }

    [Fact]
    public void A_missing_approved_image_fails_and_writes_the_received_one()
    {
        var e = Assert.Throws<ApprovalException>(() =>
            Approve.Screenshot(Scene(10, 10).ToPng(), "menu", null, Context())
        );

        Assert.Contains("no approved file", e.Message);
        Assert.True(File.Exists(PathOf("received")));
    }

    /// <summary>
    /// Writes a PNG the library's encoder never produces: another colour type or bit depth, optionally Adam7
    /// interlaced, with each row's filter cycling through None, Sub, Up, Average and Paeth.
    /// </summary>
    private static byte[] HandMadePng(
        int colorType,
        int bitDepth,
        bool interlaced,
        int width,
        int height,
        Func<int, int, int> bit
    )
    {
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            _ => 4,
        };
        var max = (1 << bitDepth) - 1;

        int Sample(int x, int y, int channel)
        {
            var on = bit(x, y);
            if (colorType == 3)
            {
                return on;
            }

            var isAlpha = (colorType == 4 && channel == 1) || (colorType == 6 && channel == 3);
            return isAlpha ? max : on * max;
        }

        byte[] Row(int y, int x0, int dx, int count)
        {
            var bits = count * channels * bitDepth;
            var row = new byte[(bits + 7) / 8];
            var bitIndex = 0;
            for (var i = 0; i < count; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var value = Sample(x0 + i * dx, y, c);
                    if (bitDepth == 16)
                    {
                        row[bitIndex / 8] = (byte)(value >> 8);
                        row[bitIndex / 8 + 1] = (byte)value;
                    }
                    else if (bitDepth == 8)
                    {
                        row[bitIndex / 8] = (byte)value;
                    }
                    else
                    {
                        row[bitIndex / 8] |= (byte)(value << (8 - bitDepth - bitIndex % 8));
                    }

                    bitIndex += bitDepth;
                }
            }

            return row;
        }

        var step = Math.Max(1, channels * bitDepth / 8);
        var raw = new MemoryStream();
        var passes = interlaced
            ? new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) }
            : new[] { (0, 0, 1, 1) };
        foreach (var (px, py, dx, dy) in passes)
        {
            var count = (width - px + dx - 1) / dx;
            byte[]? previous = null;
            var filter = 0;
            for (var y = py; y < height && count > 0; y += dy)
            {
                var row = Row(y, px, dx, count);
                var up = previous ?? new byte[row.Length];
                var filtered = new byte[row.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    int left = i >= step ? row[i - step] : 0;
                    int upLeft = i >= step ? up[i - step] : 0;
                    var predictor = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => up[i],
                        3 => (left + up[i]) / 2,
                        _ => Paeth(left, up[i], upLeft),
                    };
                    filtered[i] = (byte)(row[i] - predictor);
                }

                raw.WriteByte((byte)filter);
                raw.Write(filtered);
                previous = row;
                filter = (filter + 1) % 5;
            }
        }

        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = (byte)(interlaced ? 1 : 0);
        Chunk(png, "IHDR", header);
        if (colorType == 3)
        {
            Chunk(png, "PLTE", [0, 0, 0, 255, 255, 255]);
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw.ToArray());
        }

        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a
            : pb <= pc ? b
            : c;
    }

    // The decoder does not check CRCs, so the hand-made chunks carry zeros there.
    private static void Chunk(Stream output, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(body);
        output.Write(new byte[4]);
    }
}
