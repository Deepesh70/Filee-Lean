// Built-in vector graphics: SVG/SVGZ → PDF (vector) and → raster images with Svg.Skia and SkiaSharp, SVG ↔ SVGZ,
// raster images → SVG (tracing) and Illustrator files → PDF.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Vector.Tracing;
using SkiaSharp;

namespace Filee.Engines.Vector;

/// <summary>SVG, SVGZ and AI conversions and raster image tracing that need no external program.</summary>
public sealed class VectorConverter : IConverter, IImageTransformer
{
    public const int TraceCost = 40;

    private static readonly HashSet<string> WritableFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpeg", "jpg", "webp"
    };

    public string Id => "vector";
    public string DisplayName => "Vector graphics (built-in)";
    public int MaxParallelism => 0;

    /// <summary>Only when rendering to an image (not to PDF or between SVG and SVGZ).</summary>
    public bool Transforms(string from, string to) => WritableFormats.Contains(to);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("svg", "pdf"),
        new("svgz", "pdf"),
        new("svg", "svgz"),
        new("svgz", "svg"),
        new("svg", "png"),
        new("svg", "jpeg"),
        new("svg", "jpg"),
        new("svg", "webp"),
        new("svgz", "png"),
        new("svgz", "jpeg"),
        new("svgz", "jpg"),
        new("svgz", "webp"),
        // Illustrator files are PDF inside (or PostScript, see ConvertIllustratorAsync).
        new("ai", "pdf"),
        // Embroidery designs: their stitches as lines (DstEmbroidery), PNG / PDF / ... from there.
        new("dst", "svg"),
        // HP-GL plot files: their pen moves as lines (HpglPlot), PDF / PNG / ... from there.
        new("plt", "svg"),
        // Raster images are traced
        new("png", "svg", TraceCost, ImageSourcesOnly: true),
        new("jpeg", "svg", TraceCost, ImageSourcesOnly: true),
        new("jpg", "svg", TraceCost, ImageSourcesOnly: true),
        new("webp", "svg", TraceCost, ImageSourcesOnly: true),
        new("bmp", "svg", TraceCost, ImageSourcesOnly: true),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: $"{EngineVersions.BuiltIn} · {EngineVersions.Library("Svg.Skia", typeof(Svg.Skia.SKSvg))}");

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var written = (step.From, step.To) switch
        {
            ("ai", "pdf") => await ConvertIllustratorAsync(step, progress, cancellationToken),
            ("svg", "svgz") => await Task.Run(() => Compress(step), cancellationToken),
            ("svgz", "svg") => await Task.Run(() => Decompress(step), cancellationToken),
            ("dst", "svg") => await Task.Run(() => Embroidery(step), cancellationToken),
            ("plt", "svg") => await Task.Run(() => Plot(step), cancellationToken),
            (_, "svg") => await Task.Run(() => RasterTracing.Run(step, progress, cancellationToken), cancellationToken),
            (_, "pdf") => await Task.Run(() => ToPdf(step, cancellationToken), cancellationToken),
            _ => await Task.Run(() => ToRaster(step, progress, cancellationToken), cancellationToken),
        };
        progress?.Report(1);
        return written;
    }

    private static IReadOnlyList<string> Embroidery(ConversionStep step)
    {
        var svg = DstEmbroidery.ToSvg(File.ReadAllBytes(step.InputPath));
        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        File.WriteAllText(output, svg, new UTF8Encoding(false));
        return [output];
    }

    private static IReadOnlyList<string> Plot(ConversionStep step)
    {
        var svg = HpglPlot.ToSvg(File.ReadAllBytes(step.InputPath));
        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        File.WriteAllText(output, svg, new UTF8Encoding(false));
        return [output];
    }

    private static IReadOnlyList<string> ToPdf(ConversionStep step, CancellationToken ct)
    {
        using var drawing = SvgRenderer.Load(step.InputPath);
        ct.ThrowIfCancellationRequested();
        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        SvgRenderer.WritePdf(drawing, output, FormatRegistry.NameWithoutExtension(step.InputPath), step.Preset.Document.PdfA);
        return [output];
    }

    private static IReadOnlyList<string> ToRaster(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        using var drawing = SvgRenderer.Load(step.InputPath);
        progress?.Report(0.2);
        var scale = SvgRenderer.RasterScale(drawing.Bounds, step.Preset.Image, step.Preset.Pdf);
        using var bitmap = SvgRenderer.Rasterize(drawing, scale);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        var format = step.To.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => SKEncodedImageFormat.Jpeg,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };
        var quality = step.Preset.Image.Quality > 0 ? step.Preset.Image.Quality : 90;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        if (data is null)
            throw new InvalidOperationException($"Failed to encode raster image to {step.To}.");
        using (var fs = File.Create(output))
            data.SaveTo(fs);
        return [output];
    }

    private static IReadOnlyList<string> Compress(ConversionStep step)
    {
        var bytes = File.ReadAllBytes(step.InputPath);
        if (!SvgRenderer.IsGzip(bytes))
            RequireSvgText(bytes);
        var output = step.Output.Allocate("svgz");
        if (output is null)
            return [];
        if (SvgRenderer.IsGzip(bytes))
        {
            File.WriteAllBytes(output, bytes);
            return [output];
        }
        using (var file = File.Create(output))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(bytes);
        return [output];
    }

    private static IReadOnlyList<string> Decompress(ConversionStep step)
    {
        var bytes = SvgRenderer.ReadSvgBytes(step.InputPath);
        RequireSvgText(bytes);
        var output = step.Output.Allocate("svg");
        if (output is null)
            return [];
        File.WriteAllBytes(output, bytes);
        return [output];
    }

    /// <summary>Guards SVG ↔ SVGZ, which never parse the drawing, against renamed non-SVG files.</summary>
    private static void RequireSvgText(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (!head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is not an SVG drawing (no <svg> element).");
    }

    // ───────────────────────── Illustrator ─────────────────────────

    private static Task<IReadOnlyList<string>> ConvertIllustratorAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var kind = IllustratorKind(step.InputPath);
        if (kind == AiKind.Unknown)
            throw new InvalidDataException("The file is not an Adobe Illustrator document (neither PDF nor PostScript inside).");

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return Task.FromResult<IReadOnlyList<string>>([]);

        if (kind == AiKind.Pdf)
        {
            File.Copy(step.InputPath, output, overwrite: true);
            return Task.FromResult<IReadOnlyList<string>>([output]);
        }

        throw new NotSupportedException(
            "This Illustrator file was saved without PDF compatibility (PostScript), which is not supported in the lean configuration.");
    }

    internal enum AiKind
    {
        Unknown,
        Pdf,
        PostScript,
    }

    internal static AiKind IllustratorKind(string path)
    {
        var buffer = new byte[1024];
        int read;
        using (var file = File.OpenRead(path))
            read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var head = buffer.AsSpan(0, read);
        if (head.IndexOf("%PDF-"u8) >= 0)
            return AiKind.Pdf;
        if (head.StartsWith("%!PS"u8) || head.StartsWith((ReadOnlySpan<byte>)[0xC5, 0xD0, 0xD3, 0xC6]))
            return AiKind.PostScript;
        return AiKind.Unknown;
    }
}
