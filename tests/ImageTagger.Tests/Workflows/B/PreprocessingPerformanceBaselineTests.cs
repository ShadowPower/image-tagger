using System.Diagnostics;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

[Trait("Category", TestCategories.Integration)]
public sealed class PreprocessingPerformanceBaselineTests
{
    private readonly ITestOutputHelper _output;

    public PreprocessingPerformanceBaselineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Landscape_fixture_has_a_repeatable_time_and_allocation_ceiling()
    {
        const int iterations = 3;
        const double maximumAverageMilliseconds = 2_000;
        const long maximumManagedBytesPerImage = 128L * 1024 * 1024;
        var pipeline = new PreprocessingPipelineCompiler().ValidateAndCompile(
            PreprocessingPipelineCompilerTests.ValidPipeline(),
            PreprocessingPipelineCompilerTests.Input(448));
        var executor = new WdPreprocessingExecutor();
        var source = new FileImageSource(Path.Combine(
            PreprocessingGoldenTests.FixturesDir, "landscape_rgb.png"));
        var destination = new float[pipeline.PerSampleTensorContract.ElementCount];

        await executor.PreprocessIntoAsync(
            pipeline, source, destination, TestContext.Current.CancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await executor.PreprocessIntoAsync(
                pipeline, source, destination, TestContext.Current.CancellationToken);
        }
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var averageMilliseconds = stopwatch.Elapsed.TotalMilliseconds / iterations;
        var bytesPerImage = allocated / iterations;
        _output.WriteLine(
            $"preprocessing baseline: avg={averageMilliseconds:F2} ms, managed={bytesPerImage:N0} bytes/image, " +
            $"fixture=landscape_rgb.png, tensor=3x448x448, iterations={iterations}");

        Assert.InRange(averageMilliseconds, 0, maximumAverageMilliseconds);
        Assert.InRange(bytesPerImage, 0, maximumManagedBytesPerImage);
    }
}
