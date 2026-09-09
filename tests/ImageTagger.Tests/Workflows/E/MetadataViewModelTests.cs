using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.E;

public sealed class MetadataViewModelTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Lazy_load_parses_only_the_current_item()
    {
        using var temp = new TempDirectory("metadata-lazy");
        var first = Document(temp, "first.png");
        var second = Document(temp, "second.png");
        var parser = new CountingParser(image => Info(image.Id == first.Id ? "first-positive" : "second-positive"));
        var viewModel = new MetadataViewModel(parser);

        await viewModel.SetCurrentAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal(1, parser.CallCount);
        Assert.Equal(1, viewModel.CachedCount);
        Assert.True(viewModel.IsParsed);
        Assert.False(viewModel.IsParsing);
        Assert.Equal("first-positive", viewModel.Positive);
        Assert.Equal("AUTOMATIC1111", viewModel.SourceBadge);
        Assert.False(viewModel.HasPartialWarning);
        Assert.Same(parser.ResultFor(first.Id), first.Generation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Stale_completion_is_discarded_from_display_but_cached()
    {
        using var temp = new TempDirectory("metadata-race");
        var first = Document(temp, "first.png");
        var second = Document(temp, "second.png");
        var parser = new DeferredParser(second.Id, Info("second-positive"));
        var viewModel = new MetadataViewModel(parser);

        var pendingFirst = viewModel.SetCurrentAsync(first, TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsParsing);

        await viewModel.SetCurrentAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal("second-positive", viewModel.Positive);

        parser.Complete(first.Id, Info("first-positive"));
        await pendingFirst;

        Assert.Equal(2, parser.CallCount);
        Assert.Equal("second-positive", viewModel.Positive);
        Assert.Equal("first-positive", first.Generation?.PositivePrompt);
        Assert.Equal("second-positive", second.Generation?.PositivePrompt);

        await viewModel.SetCurrentAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(2, parser.CallCount);
        Assert.Equal("first-positive", viewModel.Positive);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Cache_hit_does_not_parse_twice_and_clear_resets_display()
    {
        using var temp = new TempDirectory("metadata-cache");
        var image = Document(temp, "cached.png");
        var parser = new CountingParser(_ => Info("cached-positive", ["注意：纯文本展示"]));
        var viewModel = new MetadataViewModel(parser);

        await viewModel.SetCurrentAsync(image, TestContext.Current.CancellationToken);
        await viewModel.SetCurrentAsync(image, TestContext.Current.CancellationToken);

        Assert.Equal(1, parser.CallCount);
        Assert.Equal("cached-positive", viewModel.Positive);
        Assert.True(viewModel.HasPartialWarning);
        Assert.Equal(["注意：纯文本展示"], viewModel.Warnings);
        Assert.Contains("comment", viewModel.RawText, StringComparison.Ordinal);

        await viewModel.SetCurrentAsync(null, TestContext.Current.CancellationToken);
        Assert.False(viewModel.IsParsed);
        Assert.Equal("未解析", viewModel.SourceBadge);
        Assert.Equal(string.Empty, viewModel.Positive);
    }

    private static ImageDocument Document(TempDirectory temp, string name)
    {
        var path = temp.WriteFile(name, [1, 2, 3]);
        return new ImageDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            CanonicalPath = path,
            FileName = name,
            FileSize = 3,
            Format = "png",
            PixelWidth = 1,
            PixelHeight = 1,
        };
    }

    private static GenerationInfo Info(string positive, IReadOnlyList<string>? warnings = null) => new()
    {
        Source = GenerationSource.Automatic1111,
        PositivePrompt = positive,
        NegativePrompt = "lowres",
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["steps"] = "28" },
        Resources = [],
        RawEntries = new Dictionary<string, string>(StringComparer.Ordinal) { ["comment"] = positive },
        Warnings = warnings ?? [],
    };

    private sealed class CountingParser(Func<ImageDocument, GenerationInfo?> results) : IGenerationInfoParser
    {
        private readonly Dictionary<string, GenerationInfo?> _results = new(StringComparer.Ordinal);

        public int CallCount;

        public GenerationInfo? ResultFor(string imageId) => _results[imageId];

        public Task<GenerationInfo?> ParseAsync(ImageDocument image, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            var info = results(image);
            _results[image.Id] = info;
            return Task.FromResult(info);
        }
    }

    private sealed class DeferredParser(string immediateId, GenerationInfo immediate) : IGenerationInfoParser
    {
        private readonly Dictionary<string, TaskCompletionSource<GenerationInfo?>> _gates = new(StringComparer.Ordinal);

        public int CallCount;

        public Task<GenerationInfo?> ParseAsync(ImageDocument image, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            if (string.Equals(image.Id, immediateId, StringComparison.Ordinal))
            {
                image.Generation = immediate;
                return Task.FromResult<GenerationInfo?>(immediate);
            }

            var gate = new TaskCompletionSource<GenerationInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gates)
            {
                _gates[image.Id] = gate;
            }

            return gate.Task;
        }

        public void Complete(string imageId, GenerationInfo? info)
        {
            lock (_gates)
            {
                _gates[imageId].TrySetResult(info);
            }
        }
    }
}
