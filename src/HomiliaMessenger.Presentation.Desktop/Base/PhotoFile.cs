using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Base;

/// <summary>
/// Reading and previewing the JPEG or PNG photos chosen for contacts and the user's profile.
/// </summary>
internal static class PhotoFile
{
    internal const int MAX_BYTES = 8 * 1024 * 1024;

    /// <summary>
    /// Reads a chosen photo, stopping as soon as it is known to be too large.
    /// </summary>
    /// <exception cref="InvalidDataException">The photo is too large, or is not a JPEG or PNG.</exception>
    internal static async Task<byte[]> ReadAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek && stream.Length - stream.Position > MAX_BYTES)
        {
            throw new InvalidDataException("Photos must be 8 MiB or smaller.");
        }

        // Read at most one byte past the limit so an oversized stream is rejected without buffering all of it.
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MAX_BYTES + 1 - buffer.Length)))) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MAX_BYTES)
            {
                throw new InvalidDataException("Photos must be 8 MiB or smaller.");
            }
        }

        byte[] bytes = buffer.ToArray();
        EnsureSupported(bytes);

        return bytes;
    }

    /// <exception cref="InvalidDataException">The photo is too large, or is not a JPEG or PNG.</exception>
    internal static void EnsureSupported(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length > MAX_BYTES)
        {
            throw new InvalidDataException("Photos must be 8 MiB or smaller.");
        }

        if (!IsJpeg(bytes) && !IsPng(bytes))
        {
            throw new InvalidDataException("Select a JPEG or PNG photo.");
        }
    }

    /// <summary>
    /// A preview of the photo, or <see langword="null"/> when this platform cannot decode it.
    /// </summary>
    internal static Bitmap? TryPreview(byte[] bytes)
    {
        try
        {
            using MemoryStream stream = new(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    internal static string Describe(byte[]? bytes, bool hasPreview)
    {
        if (bytes is null)
        {
            return "No photo";
        }

        return hasPreview
            ? $"{(IsPng(bytes) ? "PNG" : "JPEG")} photo ({bytes.Length / 1024} KiB)"
            : "Photo saved; preview unavailable";
    }

    private static bool IsPng(byte[] data)
    {
        return data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    }

    private static bool IsJpeg(byte[] data)
    {
        return data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff;
    }
}
