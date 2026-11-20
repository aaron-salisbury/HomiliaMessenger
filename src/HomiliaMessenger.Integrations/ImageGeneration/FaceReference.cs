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

    /// <summary>
    /// The largest side of a reference image: FLUX.2 [klein] draws selfies at about one megapixel, and in the GPU image
    /// experiment kept faces as well from 512-pixel photos as from larger ones.
    /// </summary>
    internal const int MAX_REFERENCE_SIDE = 1024;

    /// <summary>
    /// The smallest side a reference image is scaled up to.
    /// </summary>
    internal const int MIN_REFERENCE_SIDE = 64;

    /// <summary>
    /// Turns a contact's photo into a reference image for a model that draws from the photo itself: the whole photo, scaled
    /// down to fit <see cref="MAX_REFERENCE_SIDE"/> when larger, with sides in multiples of 16 as the model's latent grid
    /// needs, as RGB pixels.
    /// </summary>
    /// <exception cref="InvalidDataException">The photo cannot be read as an image.</exception>
    internal static GeneratedImage WholePhoto(byte[] photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        using SKBitmap? decoded = SKBitmap.Decode(photo);

        if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
        {
            throw new InvalidDataException("The contact's photo could not be read.");
        }

        // Down to the largest side allowed, or up to a sensible smallest one for a tiny photo.
        double scale = Math.Min((double)MAX_REFERENCE_SIDE / Math.Max(decoded.Width, decoded.Height), Math.Max(1d, (double)MIN_REFERENCE_SIDE / Math.Min(decoded.Width, decoded.Height)));
        int width = Math.Max(16, (int)Math.Round(decoded.Width * scale / 16) * 16);
        int height = Math.Max(16, (int)Math.Round(decoded.Height * scale / 16) * 16);
        SKImageInfo target = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap scaled = new(target);
        using (SKCanvas canvas = new(scaled))
        {
            canvas.Clear(SKColors.White);
            using SKImage image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, SKRect.Create(decoded.Width, decoded.Height), SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        ReadOnlySpan<byte> rgba = scaled.GetPixelSpan();
        byte[] rgb = new byte[width * height * 3];

        for (int pixel = 0, source = 0; pixel < rgb.Length; pixel += 3, source += 4)
        {
            rgb[pixel] = rgba[source];
            rgb[pixel + 1] = rgba[source + 1];
            rgb[pixel + 2] = rgba[source + 2];
        }

        return new GeneratedImage(width, height, 3, rgb);
    }
}
