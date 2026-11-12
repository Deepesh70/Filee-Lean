// Raster image → SVG step of the vector engine: reads the picture with SkiaSharp,
// reduces very large pictures, traces them and writes the SVG at the picture's own pixel size.

using System.Globalization;
using Filee.Core.Conversion;
using SkiaSharp;

namespace Filee.Engines.Vector.Tracing;

internal static class RasterTracing
{
    /// <summary>
    /// Pictures larger than this are reduced before tracing (time and memory grow with the pixel count, and outlines
    /// of a 24-megapixel photo would not get better). The SVG keeps the original size.
    /// </summary>
    public const int MaxPixels = 6_000_000;

    /// <summary>Longest side traced; longer pictures are reduced like <see cref="MaxPixels"/>.</summary>
    public const int MaxSide = 4096;

    public static IReadOnlyList<string> Run(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        int width, height, tracedWidth, tracedHeight;
        byte[] pixels;
        using (var codec = SKCodec.Create(step.InputPath))
        {
            if (codec is null)
                throw new InvalidDataException("The image could not be read.");
            width = codec.Info.Width;
            height = codec.Info.Height;
            (tracedWidth, tracedHeight) = TracedSize(width, height);
            var info = new SKImageInfo(tracedWidth, tracedHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            if (tracedWidth == width && tracedHeight == height)
            {
                var decodeResult = codec.GetPixels(info, bitmap.GetPixels());
                if (decodeResult != SKCodecResult.Success && decodeResult != SKCodecResult.IncompleteInput)
                    throw new InvalidDataException($"Failed to decode image pixels: {decodeResult}");
            }
            else
            {
                using var fullBmp = SKBitmap.Decode(codec);
                if (fullBmp is null)
                    throw new InvalidDataException("The image could not be decoded.");
                fullBmp.ScalePixels(bitmap, new SKSamplingOptions(SKFilterMode.Linear));
            }
            pixels = bitmap.Bytes;
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.1);

        var settings = TraceSettings.From(step.Preset.Trace, Math.Max(tracedWidth, tracedHeight));
        var result = ImageTracer.Trace(pixels, tracedWidth, tracedHeight, settings,
            progress is null ? null : new Progress<double>(p => progress.Report(0.1 + 0.85 * p)), ct);

        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        var comment = tracedWidth == width && tracedHeight == height
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"Traced by Filee from a {width} x {height} px image reduced to {tracedWidth} x {tracedHeight} px.");
        using (var stream = File.Create(output))
            ImageTracer.WriteSvg(result, stream, width, height, comment);
        return [output];
    }

    /// <summary>The size a <paramref name="width"/> × <paramref name="height"/> picture is traced at.</summary>
    internal static (int Width, int Height) TracedSize(int width, int height)
    {
        var scale = Math.Min(1, Math.Min(MaxSide / (double)Math.Max(width, height), Math.Sqrt(MaxPixels / ((double)width * height))));
        if (scale >= 1)
            return (width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
