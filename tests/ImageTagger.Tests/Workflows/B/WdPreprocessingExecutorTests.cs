using System.Buffers;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestData;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class WdPreprocessingExecutorTests
{
    [Theory]
    [MemberData(nameof(PreprocessingGoldenTests.FixtureNames), MemberType = typeof(PreprocessingGoldenTests))]
    public async Task Writes_each_fixture_directly_into_the_exact_golden_tensor(string fixtureName)
    {
        var pipeline = Pipeline();
        var actual = new float[pipeline.PerSampleTensorContract.ElementCount];
        var path = Path.Combine(PreprocessingGoldenTests.FixturesDir, fixtureName);

        await new WdPreprocessingExecutor().PreprocessIntoAsync(
            pipeline,
            new FileImageSource(path),
            actual,
            TestContext.Current.CancellationToken);

        var expected = NpyFile.Load(Path.Combine(
            PreprocessingGoldenTests.GoldenDir,
            $"{Path.GetFileNameWithoutExtension(fixtureName)}.stage_tensor.npy")).ToFloats();
        Assert.Equal(expected.Length, actual.Length);
        var tolerance = IsJpeg(fixtureName) ? 0.7f : 0f;
        Assert.All(actual.Zip(expected), pair =>
            Assert.InRange(Math.Abs(pair.First - pair.Second), 0, tolerance));
    }

    [Fact]
    public async Task Writes_only_the_target_batch_slice_and_returns_all_rented_buffers()
    {
        var pipeline = Pipeline();
        var count = pipeline.PerSampleTensorContract.ElementCount;
        var batch = Enumerable.Repeat(12345f, count * 3).ToArray();
        var pool = new TrackingBytePool();
        var path = Path.Combine(PreprocessingGoldenTests.FixturesDir, "landscape_rgb.png");

        await new WdPreprocessingExecutor(pool).PreprocessIntoAsync(
            pipeline,
            new FileImageSource(path),
            batch.AsMemory(count, count),
            TestContext.Current.CancellationToken);

        Assert.All(batch.AsSpan(0, count).ToArray(), value => Assert.Equal(12345f, value));
        Assert.All(batch.AsSpan(count * 2, count).ToArray(), value => Assert.Equal(12345f, value));
        Assert.NotEqual(12345f, batch[count]);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task Cancellation_after_a_rent_returns_the_buffer_and_commits_no_tensor()
    {
        var pipeline = Pipeline();
        var destination = Enumerable.Repeat(777f, pipeline.PerSampleTensorContract.ElementCount).ToArray();
        using var cancellation = new CancellationTokenSource();
        var pool = new TrackingBytePool(cancellation);
        var path = Path.Combine(PreprocessingGoldenTests.FixturesDir, "landscape_rgb.png");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WdPreprocessingExecutor(pool).PreprocessIntoAsync(
                pipeline,
                new FileImageSource(path),
                destination,
                cancellation.Token).AsTask());

        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.All(destination, value => Assert.Equal(777f, value));
    }

    [Fact]
    public async Task Failure_after_a_rent_returns_every_previous_buffer()
    {
        var pipeline = Pipeline();
        var destination = new float[pipeline.PerSampleTensorContract.ElementCount];
        var pool = new TrackingBytePool(throwOnRent: 2);
        var path = Path.Combine(PreprocessingGoldenTests.FixturesDir, "landscape_rgb.png");

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            new WdPreprocessingExecutor(pool).PreprocessIntoAsync(
                pipeline,
                new FileImageSource(path),
                destination,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task Rejects_a_destination_that_is_not_one_exact_sample_slice()
    {
        var pipeline = Pipeline();
        var wrong = new float[pipeline.PerSampleTensorContract.ElementCount + 1];
        var path = Path.Combine(PreprocessingGoldenTests.FixturesDir, "landscape_rgb.png");

        await Assert.ThrowsAsync<ImageTagger.Core.TaggerException>(() =>
            new WdPreprocessingExecutor().PreprocessIntoAsync(
                pipeline,
                new FileImageSource(path),
                wrong,
                TestContext.Current.CancellationToken).AsTask());
    }

    private static CompiledPreprocessingPipeline Pipeline() =>
        new PreprocessingPipelineCompiler().ValidateAndCompile(
            PreprocessingPipelineCompilerTests.ValidPipeline(),
            PreprocessingPipelineCompilerTests.Input(448));

    private static bool IsJpeg(string name) =>
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private sealed class TrackingBytePool(
        CancellationTokenSource? cancelOnFirstRent = null,
        int throwOnRent = 0) : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);

        public int RentCount { get; private set; }
        public int ReturnCount { get; private set; }
        public int OutstandingCount => _outstanding.Count;

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            if (RentCount == throwOnRent)
                throw new OutOfMemoryException("injected pool failure");
            var buffer = new byte[minimumLength];
            _outstanding.Add(buffer);
            if (RentCount == 1)
                cancelOnFirstRent?.Cancel();
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_outstanding.Remove(array), "returned buffer must have been rented exactly once");
            ReturnCount++;
        }
    }
}
