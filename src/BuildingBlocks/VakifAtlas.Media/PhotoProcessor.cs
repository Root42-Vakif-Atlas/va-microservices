using SkiaSharp;

namespace VakifAtlas.Media;

/// <summary>A re-encoded photo with its thumbnail, ready to be stored.</summary>
/// <param name="Full">JPEG bytes of the full-size image.</param>
/// <param name="Thumbnail">JPEG bytes of the thumbnail.</param>
/// <param name="Width">Width of the full-size image in pixels.</param>
/// <param name="Height">Height of the full-size image in pixels.</param>
public sealed record ProcessedPhoto(byte[] Full, byte[] Thumbnail, int Width, int Height);

/// <summary>Re-encodes uploaded JPEGs server side, stripping metadata, applying orientation and guarding against decompression bombs.</summary>
public sealed class PhotoProcessor
{
    /// <summary>Maximum length in pixels of the longer edge of the full-size image.</summary>
    public const int MaxDimension = 1600;

    /// <summary>Width in pixels of the generated thumbnail.</summary>
    public const int ThumbnailWidth = 640;
    private const int Quality = 85;

    /// <summary>Largest accepted source size in pixels, enough for a 48 MP camera image.</summary>
    public const long MaxSourcePixels = 50_000_000;

    private static readonly SemaphoreSlim Concurrency = new(Math.Max(1, Environment.ProcessorCount));

    /// <summary>Reads, validates and re-encodes an image to JPEG, returning null when it is too large or cannot be decoded.</summary>
    /// <param name="jpeg">Source image stream.</param>
    /// <param name="maxBytes">Maximum number of bytes to read from the stream.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="allowPngAndWebp">Also accept PNG and WebP sources (staff imports); user uploads stay JPEG only.</param>
    public async Task<ProcessedPhoto?> ProcessAsync(Stream jpeg, long maxBytes, CancellationToken cancellationToken,
        bool allowPngAndWebp = false)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await jpeg.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;

        await Concurrency.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Process(buffer, allowPngAndWebp), cancellationToken);
        }
        finally
        {
            Concurrency.Release();
        }
    }

    /// <summary>Checks whether the bytes start with the JPEG start-of-image marker.</summary>
    /// <param name="prefix">Leading bytes of the file.</param>
    public static bool HasJpegSignature(ReadOnlySpan<byte> prefix) => prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xD8;

    private static ProcessedPhoto? Process(Stream data, bool allowPngAndWebp)
    {
        using var codec = SKCodec.Create(data);
        if (codec is null || !(codec.EncodedFormat == SKEncodedImageFormat.Jpeg ||
                               (allowPngAndWebp && codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)))
        {
            return null;
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > MaxSourcePixels)
        {
            return null;
        }

        // Smallest decode scale that keeps the longer edge at or above the target.
        var desiredScale = Math.Min(1f, (float)MaxDimension / Math.Max(width, height));
        var scaled = codec.GetScaledDimensions(desiredScale);
        if (Math.Max(scaled.Width, scaled.Height) < Math.Min(MaxDimension, Math.Max(width, height)))
        {
            scaled = codec.Info.Size;
        }

        using var decoded = SKBitmap.Decode(codec, codec.Info.WithSize(scaled.Width, scaled.Height));
        if (decoded is null)
        {
            return null;
        }

        using var oriented = Orient(decoded, codec.EncodedOrigin);
        using var full = ResizeToFit(oriented, MaxDimension, MaxDimension);
        using var thumbnail = ResizeToFit(oriented, ThumbnailWidth, int.MaxValue);
        return new ProcessedPhoto(Encode(full), Encode(thumbnail), full.Width, full.Height);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return encoded.ToArray();
    }

    private static SKBitmap ResizeToFit(SKBitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        if (scale >= 1)
        {
            return source.Copy();
        }

        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(result);
        // JPEG has no alpha: flatten transparent PNG/WebP pixels onto white instead of black.
        canvas.Clear(SKColors.White);
        var (w, h) = ((float)result.Width, (float)result.Height);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, w / 2, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, w / 2, h / 2); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, h / 2); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, w / 2); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); canvas.Scale(1, -1, 0, w / 2); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
            default: break;
        }

        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return result;
    }
}
