// Image transcoding and transformation using SkiaSharp.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Images;

/// <summary>
/// High-speed in-memory image converter using SkiaSharp (Google Skia).
/// Handles PNG, JPG, JPEG, and WEBP transcoding, transformations, and resizing.
/// Decodes PNG, JPG, JPEG, WEBP, BMP, GIF, and ICO.
/// </summary>
public sealed class SkiaImageConverter : IConverter, IImageTransformer, IImageJoiner
{
    public string Id => "skia";
    public string DisplayName => "SkiaSharp";
    public int MaxParallelism => 0;

    private static readonly string[] Sources = ["png", "jpg", "jpeg", "webp", "bmp", "gif", "ico"];
    private static readonly string[] Targets = ["png", "jpg", "jpeg", "webp"];

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in Sources
         from target in Targets
         select new ConversionEdge(source, target, Cost: 10)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available("SkiaSharp", typeof(SKBitmap).Assembly.GetName().Version?.ToString() ?? "3.119.4");

    public bool Transforms(string from, string to) => true;

    public bool CanRead(string format) => Sources.Contains(format.ToLowerInvariant());

    public bool CanWrite(string format) => Targets.Contains(format.ToLowerInvariant());

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.1);

            using var codec = SKCodec.Create(step.InputPath);
            if (codec == null)
                throw new InvalidOperationException($"Cannot decode image from {step.InputPath}");

            var origin = codec.EncodedOrigin;
            var current = SKBitmap.Decode(codec);
            if (current == null)
                throw new InvalidOperationException($"Failed to decode bitmap from {step.InputPath}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.3);

                var oriented = ApplyExifOrientation(current, origin);
                if (!ReferenceEquals(oriented, current))
                {
                    current.Dispose();
                    current = oriented;
                }

                var transformed = ApplyTransformations(current, step.Preset.Image);
                if (!ReferenceEquals(transformed, current))
                {
                    current.Dispose();
                    current = transformed;
                }

                var resized = ApplyResize(current, step.Preset.Image);
                if (!ReferenceEquals(resized, current))
                {
                    current.Dispose();
                    current = resized;
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.7);

                var outputPath = step.Output.Allocate(step.To);
                if (outputPath == null)
                    return Array.Empty<string>();

                SaveBitmap(current, outputPath, step.To, step.Preset.Image);

                progress?.Report(1.0);
                return [outputPath];
            }
            finally
            {
                current.Dispose();
            }
        }, cancellationToken);

    public Task JoinAsync(IReadOnlyList<(string Path, string Format)> inputs, string outputPath, string format,
        ImageOptions options, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inputs.Count == 0)
                throw new InvalidOperationException("No images provided to join.");

            var bitmaps = new List<SKBitmap>();
            try
            {
                double stepProgress = 0.5 / inputs.Count;
                for (int i = 0; i < inputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (path, _) = inputs[i];
                    using var codec = SKCodec.Create(path);
                    if (codec == null)
                        throw new InvalidOperationException($"Cannot read image: {path}");

                    var origin = codec.EncodedOrigin;
                    var raw = SKBitmap.Decode(codec);
                    if (raw == null)
                        throw new InvalidOperationException($"Cannot decode image: {path}");

                    var oriented = ApplyExifOrientation(raw, origin);
                    if (!ReferenceEquals(oriented, raw))
                        raw.Dispose();

                    bitmaps.Add(oriented);
                    progress?.Report(0.1 + (i + 1) * stepProgress);
                }

                bool horizontal = options.JoinDirection == ImageJoinDirection.Horizontal;
                long totalWidth = horizontal ? bitmaps.Sum(b => (long)b.Width) : bitmaps.Max(b => (long)b.Width);
                long totalHeight = horizontal ? bitmaps.Max(b => (long)b.Height) : bitmaps.Sum(b => (long)b.Height);

                const long maxDimension = 32767;
                if (totalWidth > maxDimension || totalHeight > maxDimension)
                    throw new InvalidOperationException($"Combined image size ({totalWidth}x{totalHeight}) exceeds max limit of {maxDimension}px.");

                var info = new SKImageInfo((int)totalWidth, (int)totalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                if (surface == null)
                    throw new InvalidOperationException($"Failed to allocate surface for size {totalWidth}x{totalHeight}.");

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                int offset = 0;
                for (int i = 0; i < bitmaps.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bmp = bitmaps[i];
                    if (horizontal)
                    {
                        canvas.DrawBitmap(bmp, offset, 0);
                        offset += bmp.Width;
                    }
                    else
                    {
                        canvas.DrawBitmap(bmp, 0, offset);
                        offset += bmp.Height;
                    }
                }

                progress?.Report(0.8);

                using var finalImage = surface.Snapshot();
                using var finalBmp = SKBitmap.FromImage(finalImage);
                SaveBitmap(finalBmp, outputPath, format, options);

                progress?.Report(1.0);
            }
            finally
            {
                foreach (var b in bitmaps)
                    b.Dispose();
            }
        }, cancellationToken);

    private static void SaveBitmap(SKBitmap bitmap, string outputPath, string targetFormat, ImageOptions options)
    {
        var fmt = targetFormat.ToLowerInvariant() switch
        {
            "png" => SKEncodedImageFormat.Png,
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "webp" => SKEncodedImageFormat.Webp,
            _ => throw new NotSupportedException($"Target format '{targetFormat}' is not supported by SkiaSharp encoder.")
        };

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        int quality = options.Quality is > 0 and <= 100 ? options.Quality : 90;

        SKBitmap toEncode = bitmap;
        SKBitmap? composited = null;
        try
        {
            if (fmt == SKEncodedImageFormat.Jpeg)
            {
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                composited = new SKBitmap(info);
                using var canvas = new SKCanvas(composited);
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bitmap, 0, 0);
                toEncode = composited;
            }

            using var data = toEncode.Encode(fmt, quality)
                ?? SKImage.FromBitmap(toEncode).Encode(fmt, quality);
            if (data == null)
                throw new InvalidOperationException($"Failed to encode bitmap to {fmt}.");

            using var stream = File.Create(outputPath);
            data.SaveTo(stream);
        }
        finally
        {
            composited?.Dispose();
        }
    }

    private static SKBitmap ApplyTransformations(SKBitmap src, ImageOptions options)
    {
        var rotation = options.Rotation;
        var flip = options.Flip;
        if (rotation == ImageRotation.None && flip == ImageFlip.None)
            return src;

        int angle = rotation switch
        {
            ImageRotation.Right90 => 90,
            ImageRotation.Rotate180 => 180,
            ImageRotation.Left90 => 270,
            _ => 0
        };

        bool flipH = flip == ImageFlip.Horizontal;
        bool flipV = flip == ImageFlip.Vertical;

        int newWidth = (angle == 90 || angle == 270) ? src.Height : src.Width;
        int newHeight = (angle == 90 || angle == 270) ? src.Width : src.Height;

        var result = new SKBitmap(src.Info.WithSize(newWidth, newHeight));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        canvas.Translate(newWidth / 2f, newHeight / 2f);
        if (flipH || flipV)
            canvas.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        if (angle != 0)
            canvas.RotateDegrees(angle);

        canvas.DrawBitmap(src, -src.Width / 2f, -src.Height / 2f);
        return result;
    }

    private static SKBitmap ApplyResize(SKBitmap src, ImageOptions options)
    {
        int targetWidth = src.Width;
        int targetHeight = src.Height;

        if (options.CustomSize is { Width: > 0, Height: > 0 } custom)
        {
            if (custom.LockAspect)
            {
                double ratio = Math.Min((double)custom.Width / src.Width, (double)custom.Height / src.Height);
                targetWidth = Math.Max(1, (int)Math.Round(src.Width * ratio));
                targetHeight = Math.Max(1, (int)Math.Round(src.Height * ratio));
            }
            else
            {
                targetWidth = Math.Max(1, custom.Width);
                targetHeight = Math.Max(1, custom.Height);
            }
        }
        else if (options.Resize == ResizeMode.Percent && options.ResizePercent > 0)
        {
            targetWidth = Math.Max(1, src.Width * options.ResizePercent / 100);
            targetHeight = Math.Max(1, src.Height * options.ResizePercent / 100);
        }
        else if (options.Resize == ResizeMode.Width && options.Width > 0 && src.Width != options.Width)
        {
            double ratio = (double)options.Width / src.Width;
            targetWidth = options.Width;
            targetHeight = Math.Max(1, (int)Math.Round(src.Height * ratio));
        }
        else if (options.Resize == ResizeMode.LongEdge && options.LongEdge > 0)
        {
            int longEdge = Math.Max(src.Width, src.Height);
            if (longEdge > options.LongEdge)
            {
                double ratio = (double)options.LongEdge / longEdge;
                targetWidth = Math.Max(1, (int)Math.Round(src.Width * ratio));
                targetHeight = Math.Max(1, (int)Math.Round(src.Height * ratio));
            }
        }

        if (targetWidth == src.Width && targetHeight == src.Height)
            return src;

        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        var newInfo = src.Info.WithSize(targetWidth, targetHeight);
        return src.Resize(newInfo, sampling)
            ?? throw new InvalidOperationException($"Failed to resize bitmap to {targetWidth}x{targetHeight}.");
    }

    private static SKBitmap ApplyExifOrientation(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
            return src;

        int angle = 0;
        bool flipH = false;
        bool flipV = false;

        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // 2: Flip Horizontal
                flipH = true;
                break;
            case SKEncodedOrigin.BottomRight: // 3: Rotate 180
                angle = 180;
                break;
            case SKEncodedOrigin.BottomLeft: // 4: Flip Vertical
                flipV = true;
                break;
            case SKEncodedOrigin.LeftTop: // 5: Transpose (Rotate 90 + Flip H)
                angle = 90;
                flipH = true;
                break;
            case SKEncodedOrigin.RightTop: // 6: Rotate 90
                angle = 90;
                break;
            case SKEncodedOrigin.RightBottom: // 7: Transverse (Rotate 270 + Flip H)
                angle = 270;
                flipH = true;
                break;
            case SKEncodedOrigin.LeftBottom: // 8: Rotate 270
                angle = 270;
                break;
        }

        int newWidth = (angle == 90 || angle == 270) ? src.Height : src.Width;
        int newHeight = (angle == 90 || angle == 270) ? src.Width : src.Height;

        var result = new SKBitmap(src.Info.WithSize(newWidth, newHeight));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        canvas.Translate(newWidth / 2f, newHeight / 2f);
        if (flipH || flipV)
            canvas.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        if (angle != 0)
            canvas.RotateDegrees(angle);

        canvas.DrawBitmap(src, -src.Width / 2f, -src.Height / 2f);
        return result;
    }
}
