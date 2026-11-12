using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Images;
using SkiaSharp;
using Xunit;

namespace Filee.Engines.Tests;

public class SkiaImageConverterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string CreateTestImage(string name, int width, int height, SKColor color, SKEncodedImageFormat format)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void Targets_DoNotInclude_BmpOrIco()
    {
        var converter = new SkiaImageConverter();
        Assert.DoesNotContain(converter.Edges, e => e.To == "bmp");
        Assert.DoesNotContain(converter.Edges, e => e.To == "ico");
        Assert.Contains(converter.Edges, e => e.From == "bmp");
        Assert.Contains(converter.Edges, e => e.From == "ico");
    }

    [Fact]
    public async Task Transcode_Png_To_Jpeg_Succeeds()
    {
        var input = CreateTestImage("test.png", 100, 100, SKColors.CornflowerBlue, SKEncodedImageFormat.Png);
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "jpg" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.True(File.Exists(output));
        Assert.EndsWith(".jpg", output, StringComparison.OrdinalIgnoreCase);

        using var resultBmp = SKBitmap.Decode(output);
        Assert.NotNull(resultBmp);
        Assert.Equal(100, resultBmp.Width);
        Assert.Equal(100, resultBmp.Height);
    }

    [Fact]
    public async Task Transcode_Png_To_Webp_Succeeds()
    {
        var input = CreateTestImage("test.png", 50, 80, SKColors.Crimson, SKEncodedImageFormat.Png);
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "webp" });
        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        Assert.True(File.Exists(output));
        Assert.EndsWith(".webp", output, StringComparison.OrdinalIgnoreCase);

        using var resultBmp = SKBitmap.Decode(output);
        Assert.NotNull(resultBmp);
        Assert.Equal(50, resultBmp.Width);
        Assert.Equal(80, resultBmp.Height);
    }

    [Fact]
    public async Task Resize_Percent_ScalesDown_Correctly()
    {
        var input = CreateTestImage("big.png", 200, 200, SKColors.SeaGreen, SKEncodedImageFormat.Png);
        var preset = new Preset
        {
            TargetFormat = "png",
            Image = new ImageOptions
            {
                Resize = ResizeMode.Percent,
                ResizePercent = 50
            }
        };
        var job = await fx.ConvertAsync([input], preset);
        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        using var resultBmp = SKBitmap.Decode(output);
        Assert.NotNull(resultBmp);
        Assert.Equal(100, resultBmp.Width);
        Assert.Equal(100, resultBmp.Height);
    }

    [Fact]
    public async Task Rotate_Right90_SwapsDimensions_Correctly()
    {
        var input = CreateTestImage("rect.png", 120, 60, SKColors.DarkOrange, SKEncodedImageFormat.Png);
        var preset = new Preset
        {
            TargetFormat = "png",
            Image = new ImageOptions
            {
                Rotation = ImageRotation.Right90
            }
        };
        var job = await fx.ConvertAsync([input], preset);
        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        using var resultBmp = SKBitmap.Decode(output);
        Assert.NotNull(resultBmp);
        Assert.Equal(60, resultBmp.Width);
        Assert.Equal(120, resultBmp.Height);
    }
}
