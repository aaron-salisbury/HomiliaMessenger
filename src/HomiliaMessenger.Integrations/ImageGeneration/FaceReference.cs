using SkiaSharp;
using System;
using System.IO;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Turns a contact's photo into the face face matching keeps: its centered square, as 512×512 RGB pixels, which is how the
/// local selfie experiment's reference portraits were given to IP-Adapter.
/// </summary>
internal static class FaceReference
{
    internal const int SIZE = 512;

    /// <exception cref="InvalidDataException">The photo cannot be read as an image.</exception>
    internal static GeneratedImage FromPhoto(byte[] photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        using SKBitmap? decoded = SKBitmap.Decode(photo);

        if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
        {
            throw new InvalidDataException("The contact's photo could not be read.");
        }

        int side = Math.Min(decoded.Width, decoded.Height);
        SKRectI square = SKRectI.Create((decoded.Width - side) / 2, (decoded.Height - side) / 2, side, side);
        SKImageInfo target = new(SIZE, SIZE, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap scaled = new(target);
        using (SKCanvas canvas = new(scaled))
        {
            canvas.Clear(SKColors.White);
            using SKImage image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, square, SKRect.Create(SIZE, SIZE), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        ReadOnlySpan<byte> rgba = scaled.GetPixelSpan();
        byte[] rgb = new byte[SIZE * SIZE * 3];

        for (int pixel = 0, source = 0; pixel < rgb.Length; pixel += 3, source += 4)
        {
            rgb[pixel] = rgba[source];
            rgb[pixel + 1] = rgba[source + 1];
            rgb[pixel + 2] = rgba[source + 2];
        }

        return new GeneratedImage(SIZE, SIZE, 3, rgb);
    }
}
