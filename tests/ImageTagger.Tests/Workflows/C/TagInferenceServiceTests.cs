using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class TagInferenceServiceTests
{
    [Fact]
    public async Task Infer_returns_atomic_snapshot_without_copying_label_text()
    {
        var pack = TestPack(labelCount: 6);
        var image = TestImage();
        var factory = new StubFactory(new StubHandle(labelCount: 6));
        var executor = new StubExecutor(pack.Pipeline.PerSampleTensorContract.ElementCount);
        using var service = new TagInferenceService(factory, executor);

        var snapshot = await service.InferAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.Equal(pack.Fingerprint.Value, snapshot.ModelFingerprint);
        Assert.Equal(6, snapshot.Probabilities.Length);
        Assert.Equal("fake-ort", snapshot.Runtime);
        Assert.Equal("CPU", snapshot.ExecutionProvider);
        Assert.Equal(1, snapshot.BatchSize);
        Assert.All(snapshot.Probabilities, p => Assert.InRange(p, 0f, 1f));
        // Snapshot carries only floats + metadata, never catalog strings.
        Assert.IsType<float[]>(snapshot.Probabilities);
        Assert.True(service.IsLoaded(pack.Fingerprint));
        Assert.Equal(1, executor.Calls);
        Assert.Equal(pack.Pipeline.PerSampleTensorContract.ElementCount, executor.LastDestinationLength);
        // Caller owns write-back; the service itself must not mutate the document.
        Assert.Null(image.Prediction);
    }

    [Fact]
    public async Task Output_length_mismatch_rejects_the_result()
    {
        var pack = TestPack(labelCount: 6);
        var factory = new StubFactory(new StubHandle(labelCount: 6, actualOutput: 5));
        var executor = new StubExecutor(pack.Pipeline.PerSampleTensorContract.ElementCount);
        using var service = new TagInferenceService(factory, executor);

        var exception = await Assert.ThrowsAsync<TaggerException>(
            () => service.InferAsync(TestImage(), pack, TestContext.Current.CancellationToken));
        Assert.Equal(TaggerErrorCode.ModelIncompatible, exception.Code);
    }

    [Theory]
    [InlineData("multi-label-image-tagging", "sigmoid", true)]
    [InlineData("multi-label-image-tagging", "none", true)]
    [InlineData("multi-label-image-tagging", "softmax", false)]
    [InlineData("object-detection", "sigmoid", false)]
    public void Supports_checks_task_and_activation(string task, string activation, bool expected)
    {
        var descriptor = FakeModelPack.Descriptor(activation: activation) with { Task = task };
        using var service = new TagInferenceService(
            new StubFactory(new StubHandle(6)), new StubExecutor(3 * 8 * 8));
        Assert.Equal(expected, service.Supports(descriptor));
    }

    [Fact]
    public void None_activation_copies_logits_verbatim()
    {
        using var service = new TagInferenceService(
            new StubFactory(new StubHandle(3)), new StubExecutor(3));
        var descriptor = FakeModelPack.Descriptor(activation: "none");
        float[] logits = [0.1f, 5f, -3f];
        float[] probs = new float[3];
        service.Activate(descriptor, logits, probs);
        Assert.Equal(logits, probs);
    }

    [Fact]
    public async Task Fingerprint_change_replaces_the_session_and_disposes_the_old_one()
    {
        var packA = TestPack(labelCount: 4, fingerprint: "fp-a");
        var packB = TestPack(labelCount: 4, fingerprint: "fp-b");
        var handleA = new StubHandle(4);
        var handleB = new StubHandle(4);
        var factory = new QueueFactory([handleA, handleB]);
        // Element counts differ per pack (width 8 vs default); use a flexible executor.
        var executor = new FlexibleExecutor();
        using var service = new TagInferenceService(factory, executor);

        await service.InferAsync(TestImage(), packA, TestContext.Current.CancellationToken);
        Assert.True(service.IsLoaded(packA.Fingerprint));
        await service.InferAsync(TestImage(), packB, TestContext.Current.CancellationToken);
        Assert.True(service.IsLoaded(packB.Fingerprint));
        Assert.False(service.IsLoaded(packA.Fingerprint));
        Assert.True(handleA.Disposed);
        Assert.False(handleB.Disposed);
        Assert.Equal(2, factory.Calls);
    }

    [Fact]
    public async Task Session_input_metadata_mismatch_is_rejected()
    {
        // Real reverse-check path with a tiny real ONNX is covered by smoke tests;
        // here we verify the adapter refuses unsupported contracts before any session.
        var descriptor = FakeModelPack.Descriptor() with { Task = "object-detection" };
        var pack = TestPack() with { Descriptor = descriptor };
        using var service = new TagInferenceService(
            new StubFactory(new StubHandle(6)), new StubExecutor(10));
        await Assert.ThrowsAsync<TaggerException>(
            () => service.InferAsync(TestImage(), pack, TestContext.Current.CancellationToken));
    }

    internal static LoadedModelPack TestPack(int labelCount = 6, string fingerprint = "fp-test")
    {
        var descriptor = FakeModelPack.Descriptor(labelCount: labelCount, width: 8, height: 8);
        var catalog = labelCount == 6
            ? FakeModelPack.Catalog()
            : new TagCatalog(Enumerable.Range(0, labelCount).Select(i =>
                new TagCatalogEntry(i, $"tag_{i}", null, "subject")).ToArray());
        var fingerprintRecord = FakeModelPack.Fingerprint(fingerprint) with { Value = fingerprint };
        var pipeline = new PreprocessingPipelineCompiler().ValidateAndCompile(
            descriptor.Preprocessing,
            new ModelInputTensor { DType = TensorDType.Float32, Layout = "NCHW", Shape = [3, 8, 8] });
        return new LoadedModelPack(descriptor, catalog, fingerprintRecord, pipeline);
    }

    internal static ImageDocument TestImage(string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        CanonicalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"),
        FileName = "test.png",
        FileSize = 10,
        Format = "png",
        PixelWidth = 8,
        PixelHeight = 8,
    };

    internal sealed class StubHandle(int labelCount, int? actualOutput = null) : IInferenceSessionHandle
    {
        private readonly int _expected = labelCount;
        private readonly int _actual = actualOutput ?? labelCount;
        public bool Disposed { get; private set; }
        public string Runtime => "fake-ort";
        public string ExecutionProvider => "CPU";
        public string Device => "CPU-X64";

        public void Run(ReadOnlySpan<float> batchInput, int batchSamples, Span<float> logits)
        {
            if (logits.Length != _expected)
                throw new TaggerException(TaggerErrorCode.ModelIncompatible, "test buffer mismatch");
            if (_actual != _expected)
                throw new TaggerException(
                    TaggerErrorCode.ModelIncompatible,
                    $"模型输出长度 {_actual} 与期望 {_expected} 不一致。");
            for (int i = 0; i < logits.Length; i++)
                logits[i] = (i % 5) - 2f;
        }

        public void Dispose() => Disposed = true;
    }

    internal sealed class StubExecutor(int expectedLength) : IPreprocessingExecutor
    {
        public int Calls { get; private set; }
        public int LastDestinationLength { get; private set; }

        public ValueTask PreprocessIntoAsync(
            CompiledPreprocessingPipeline pipeline,
            IImageSource source,
            Memory<float> destination,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastDestinationLength = destination.Length;
            Assert.Equal(expectedLength, destination.Length);
            destination.Span.Fill(0.1f);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FlexibleExecutor : IPreprocessingExecutor
    {
        public ValueTask PreprocessIntoAsync(
            CompiledPreprocessingPipeline pipeline,
            IImageSource source,
            Memory<float> destination,
            CancellationToken cancellationToken)
        {
            destination.Span.Fill(0.1f);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class StubFactory(IInferenceSessionHandle handle) : IInferenceRuntimeFactory
    {
        public Task<IInferenceSessionHandle> CreateAsync(
            ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken) =>
            Task.FromResult(handle);
    }

    internal sealed class QueueFactory(Queue<IInferenceSessionHandle> handles) : IInferenceRuntimeFactory
    {
        public QueueFactory(IReadOnlyList<IInferenceSessionHandle> handles)
            : this(new Queue<IInferenceSessionHandle>(handles))
        {
        }

        public int Calls { get; private set; }

        public Task<IInferenceSessionHandle> CreateAsync(
            ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handles.Dequeue());
        }
    }
}
