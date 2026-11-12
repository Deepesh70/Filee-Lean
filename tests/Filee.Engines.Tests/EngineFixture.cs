using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

/// <summary>Real engines + a temp folder with generated sample files.</summary>
public sealed class EngineFixture : IDisposable
{
    public EngineFixture()
    {
        Directory.CreateDirectory(Root);
        var env = new EngineEnvironment(Path.Combine(Root, "data"));
        Converters = EngineRegistry.CreateAll(env);
        Catalog = new ConverterCatalog(Converters) { Priority = new AppSettings().EnginePriority };
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "filee-engine-tests", Guid.NewGuid().ToString("N")[..8]);
    public IReadOnlyList<IConverter> Converters { get; }
    public ConverterCatalog Catalog { get; }

    /// <summary>Creates a fresh sub folder for one test.</summary>
    public string NewFolder()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<ConversionJob> ConvertAsync(IReadOnlyList<string> sources, Preset preset)
    {
        await using var queue = new JobQueue(Catalog, EngineRegistry.FindPdfMerger(Converters),
            combiner: EngineRegistry.FindFileCombiner(Converters), tiffMerger: EngineRegistry.FindTiffMerger(Converters),
            mediaJoiner: EngineRegistry.FindMediaJoiner(Converters));
        var job = new ConversionJob(sources, preset, preset.Name);
        await queue.RunAsync(job);
        return job;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
