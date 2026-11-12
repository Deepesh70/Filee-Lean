// Renders PDF pages to raster images using PDFium (PDFtoImage) and SkiaSharp.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>PDF → PNG, JPG, JPEG, WEBP using PDFium and SkiaSharp.</summary>
public sealed class PdfiumConverter : IConverter, IImageTransformer
{
    private static readonly string[] Targets = ["png", "jpg", "jpeg", "webp"];

    public string Id => "pdfium";
    public string DisplayName => "PDFium";
    public int MaxParallelism => 1;

    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        Targets.Select(target => new ConversionEdge("pdf", target)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(version: typeof(Conversion).Assembly.GetName().Version?.ToString() ?? "5.2.1");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(async () =>
        {
            var pdf = await File.ReadAllBytesAsync(step.InputPath, cancellationToken);
            var pageCount = Conversion.GetPageCount(pdf, null);
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, pageCount);
            var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);

            var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
            var written = new List<string>();
            var fmt = step.To.ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
                "webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png
            };
            int quality = step.Preset.Image.Quality > 0 ? step.Preset.Image.Quality : 90;

            for (int i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[i];
                using var bitmap = Conversion.ToImage(pdf, page, null, options);
                if (bitmap == null)
                    continue;

                var suffix = pages.Count > 1 ? $"_{i + 1}" : null;
                var outputPath = step.Output.Allocate(step.To, suffix);
                if (outputPath != null)
                {
                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    using var data = bitmap.Encode(fmt, quality);
                    if (data != null)
                    {
                        using var stream = File.Create(outputPath);
                        data.SaveTo(stream);
                        written.Add(outputPath);
                    }
                }
                progress?.Report((i + 1.0) / pages.Count);
            }

            return written;
        }, cancellationToken);
}
