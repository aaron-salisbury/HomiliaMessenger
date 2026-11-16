using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Writes 8-bit RGB or RGBA pixels as a PNG file, without a dependency on an imaging library.
/// </summary>
/// <remarks>
/// Adapted from the PNG writer in the local portrait experiment's in-process probe. Rows are written unfiltered and
/// compressed with zlib, as the <a href="https://www.w3.org/TR/png-3/">PNG specification</a> allows.
/// </remarks>
internal static class PngEncoder
{
    private static readonly byte[] SIGNATURE = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CRC_TABLE = CreateCrcTable();

    internal static byte[] Encode(GeneratedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Channels is not (3 or 4) || image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height * image.Channels)
        {
            throw new ArgumentException("The image's pixels do not match its size.", nameof(image));
        }

        using MemoryStream output = new();
        output.Write(SIGNATURE);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), image.Height);
        header[8] = 8;
        header[9] = image.Channels == 3 ? (byte)2 : (byte)6;
        WriteChunk(output, "IHDR", header);

        using MemoryStream compressed = new();
        int stride = image.Width * image.Channels;

        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int row = 0; row < image.Height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(image.Pixels, row * stride, stride);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);

        byte[] tag = Encoding.ASCII.GetBytes(type);
        output.Write(tag);
        output.Write(data);

        uint crc = 0xffffffff;
        crc = Update(crc, tag);
        crc = Update(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static uint Update(uint crc, byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            crc = CRC_TABLE[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
