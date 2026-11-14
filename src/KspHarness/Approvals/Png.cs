using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace KspHarness.Approvals;

/// <summary>
/// A small PNG reader and writer, so that the library needs no image package. It reads every standard PNG (all colour
/// types and bit depths, palettes, transparency chunks, interlacing) into 8-bit RGBA, and writes 8-bit RGBA.
/// </summary>
internal static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly uint[] CrcTable = BuildCrcTable();

    // Adam7 passes: x start, y start, x step, y step.
    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
    [
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    ];

    public static RgbaImage Decode(byte[] data)
    {
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG file");
        }

        var header = default(Header);
        var haveHeader = false;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();

        var position = Signature.Length;
        while (true)
        {
            if (position + 12 > data.Length)
            {
                throw new InvalidDataException("the PNG ends before its IEND chunk");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            if (length < 0 || position + 12 + length > data.Length)
            {
                throw new InvalidDataException($"the PNG chunk {type} runs past the end of the file");
            }

            var body = data.AsSpan(position + 8, length);
            position += 12 + length;

            if (type == "IEND")
            {
                break;
            }

            switch (type)
            {
                case "IHDR":
                    header = ReadHeader(body);
                    haveHeader = true;
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
            }
        }

        if (!haveHeader)
        {
            throw new InvalidDataException("the PNG has no IHDR chunk");
        }

        if (header.ColorType == 3 && palette is null)
        {
            throw new InvalidDataException("the palette PNG has no PLTE chunk");
        }

        compressed.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var image = new RgbaImage(header.Width, header.Height);
        var reader = new PixelReader(header, palette, transparency);
        var offset = 0;
        if (header.Interlaced)
        {
            foreach (var pass in Adam7)
            {
                var passWidth = (header.Width - pass.X + pass.Dx - 1) / pass.Dx;
                var passHeight = (header.Height - pass.Y + pass.Dy - 1) / pass.Dy;
                if (passWidth <= 0 || passHeight <= 0)
                {
                    continue;
                }

                offset = DecodePass(raw, offset, passWidth, passHeight, reader, image, pass);
            }
        }
        else
        {
            DecodePass(raw, 0, header.Width, header.Height, reader, image, (0, 0, 1, 1));
        }

        return image;
    }

    public static byte[] Encode(RgbaImage image)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var stride = image.Width * 4;
            var filtered = new byte[stride + 1];
            for (var y = 0; y < image.Height; y++)
            {
                // Sub filter: screenshots are mostly smooth runs, which it turns into small numbers that deflate well.
                filtered[0] = 1;
                var row = image.Pixels.AsSpan(y * stride, stride);
                for (var i = 0; i < stride; i++)
                {
                    filtered[i + 1] = (byte)(row[i] - (i >= 4 ? row[i - 4] : 0));
                }

                zlib.Write(filtered);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            _ => 4,
        };

        public int BitsPerPixel => Channels * BitDepth;
    }

    private static Header ReadHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length < 13)
        {
            throw new InvalidDataException("the PNG's IHDR chunk is too short");
        }

        var header = new Header(
            BinaryPrimitives.ReadInt32BigEndian(body),
            BinaryPrimitives.ReadInt32BigEndian(body[4..]),
            body[8],
            body[9],
            body[12] == 1);

        var depthValid = header.ColorType switch
        {
            0 => header.BitDepth is 1 or 2 or 4 or 8 or 16,
            3 => header.BitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => header.BitDepth is 8 or 16,
            _ => false,
        };
        if (!depthValid || header.Width <= 0 || header.Height <= 0)
        {
            throw new InvalidDataException(
                $"unsupported PNG: {header.Width}x{header.Height}, colour type {header.ColorType}, depth {header.BitDepth}");
        }

        return header;
    }

    private static int DecodePass(byte[] raw, int offset, int width, int height, PixelReader reader, RgbaImage image,
        (int X, int Y, int Dx, int Dy) pass)
    {
        var bitsPerPixel = reader.Header.BitsPerPixel;
        var stride = (width * bitsPerPixel + 7) / 8;
        var step = Math.Max(1, bitsPerPixel / 8);
        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            if (offset + 1 + stride > raw.Length)
            {
                throw new InvalidDataException("the PNG's image data is shorter than its size");
            }

            var filter = raw[offset];
            Array.Copy(raw, offset + 1, current, 0, stride);
            offset += 1 + stride;
            Unfilter(filter, current, previous, step);

            for (var x = 0; x < width; x++)
            {
                var target = image.Offset(pass.X + x * pass.Dx, pass.Y + y * pass.Dy);
                reader.Read(current, x, image.Pixels.AsSpan(target, 4));
            }

            (previous, current) = (current, previous);
        }

        return offset;
    }

    private static void Unfilter(byte filter, byte[] row, byte[] previous, int step)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = step; i < row.Length; i++)
                {
                    row[i] += row[i - step];
                }

                break;
            case 2:
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] += previous[i];
                }

                break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= step ? row[i - step] : 0;
                    row[i] += (byte)((left + previous[i]) / 2);
                }

                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= step ? row[i - step] : 0;
                    var upLeft = i >= step ? previous[i - step] : 0;
                    row[i] += Paeth(left, previous[i], upLeft);
                }

                break;
            default:
                throw new InvalidDataException($"unknown PNG filter type {filter}");
        }
    }

    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return (byte)a;
        }

        return pb <= pc ? (byte)b : (byte)c;
    }

    private sealed class PixelReader(Header header, byte[]? palette, byte[]? transparency)
    {
        public Header Header { get; } = header;

        public void Read(byte[] row, int x, Span<byte> rgba)
        {
            switch (Header.ColorType)
            {
                case 0:
                {
                    var gray = Sample(row, x, 0);
                    var value = Scale(gray);
                    rgba[0] = rgba[1] = rgba[2] = value;
                    rgba[3] = IsTransparent(gray, 0) ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                {
                    var r = Sample(row, x, 0);
                    var g = Sample(row, x, 1);
                    var b = Sample(row, x, 2);
                    rgba[0] = Scale(r);
                    rgba[1] = Scale(g);
                    rgba[2] = Scale(b);
                    rgba[3] = IsTransparent(r, 0) && IsTransparent(g, 1) && IsTransparent(b, 2) ? (byte)0 : (byte)255;
                    break;
                }
                case 3:
                {
                    var index = Sample(row, x, 0);
                    if (index * 3 + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException($"the PNG uses palette entry {index}, past the end of its palette");
                    }

                    rgba[0] = palette[index * 3];
                    rgba[1] = palette[index * 3 + 1];
                    rgba[2] = palette[index * 3 + 2];
                    rgba[3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                    break;
                }
                case 4:
                    rgba[0] = rgba[1] = rgba[2] = Scale(Sample(row, x, 0));
                    rgba[3] = Scale(Sample(row, x, 1));
                    break;
                default:
                    for (var c = 0; c < 4; c++)
                    {
                        rgba[c] = Scale(Sample(row, x, c));
                    }

                    break;
            }
        }

        private int Sample(byte[] row, int x, int channel)
        {
            var depth = Header.BitDepth;
            switch (depth)
            {
                case 8:
                    return row[x * Header.Channels + channel];
                case 16:
                    var at = (x * Header.Channels + channel) * 2;
                    return (row[at] << 8) | row[at + 1];
                default:
                    var bit = x * depth;
                    return (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
            }
        }

        private byte Scale(int sample)
        {
            return Header.BitDepth switch
            {
                8 => (byte)sample,
                16 => (byte)(sample >> 8),
                _ => (byte)(sample * 255 / ((1 << Header.BitDepth) - 1)),
            };
        }

        private bool IsTransparent(int sample, int channel)
        {
            return transparency is not null
                && transparency.Length >= channel * 2 + 2
                && ((transparency[channel * 2] << 8) | transparency[channel * 2 + 1]) == sample;
        }
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, body.Length);
        output.Write(buffer);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);

        var crc = Crc(0xFFFFFFFFu, typeBytes);
        crc = Crc(crc, body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
