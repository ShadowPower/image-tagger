using ImageTagger.App.Composition;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Services;
using ImageTagger.Tests.TestInfrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

[Trait("Category", TestCategories.Integration)]
public sealed class CompositionTests
{
    [Fact]
    public void Service_provider_builds_and_resolves_core_singletons()
    {
        var services = new ServiceCollection();
        services.AddImageTaggerServices();
        services.AddSingleton<IPlatformService>(_ => new HeadlessPlatformService());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ISettingsStore>());
        Assert.NotNull(provider.GetRequiredService<IModelPackService>());
        Assert.NotNull(provider.GetRequiredService<ITagInferenceService>());
        Assert.NotNull(provider.GetRequiredService<IMemoryCache>());
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
    }

    /// <summary>
    /// 回归：生产容器解析出的流水线编译器必须认识全部标准算子。
    /// MS.DI 会把可选集合依赖解析为空枚举，空注册表曾导致真实模型包
    /// 加载失败（unknown preprocessing operator 'decode'）。
    /// </summary>
    [Fact]
    [Trait("Category", TestCategories.Unit)]
    public void Production_pipeline_compiler_knows_standard_operators()
    {
        var services = new ServiceCollection();
        services.AddImageTaggerServices();
        using var provider = services.BuildServiceProvider();

        var compiler = provider.GetRequiredService<
            ImageTagger.Core.Pipelines.IPreprocessingPipelineCompiler>();
        var descriptor = ImageTagger.Core.Fakes.FakeModelPack.Descriptor();
        var modelInput = new ImageTagger.Core.Pipelines.ModelInputTensor
        {
            DType = ImageTagger.Core.Pipelines.TensorDType.Float32,
            Shape = [3, descriptor.Input.Height, descriptor.Input.Width],
            Layout = "NCHW",
        };

        var pipeline = compiler.ValidateAndCompile(descriptor.Preprocessing, modelInput);

        Assert.NotNull(pipeline);
        Assert.Equal(11, pipeline.Steps.Count);
    }

    [Fact]
    public void Production_registrations_contain_no_fake_services()
    {
        var services = new ServiceCollection();
        services.AddImageTaggerServices();

        foreach (var descriptor in services)
        {
            Assert.DoesNotContain(
                "Fake",
                descriptor.ServiceType.FullName ?? string.Empty,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                ".Fakes.",
                descriptor.ServiceType.FullName ?? string.Empty,
                StringComparison.Ordinal);
            if (descriptor.ImplementationType is not null)
            {
                Assert.DoesNotContain(
                    "Fake",
                    descriptor.ImplementationType.FullName ?? string.Empty,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    ".Fakes.",
                    descriptor.ImplementationType.FullName ?? string.Empty,
                    StringComparison.Ordinal);
            }

            if (descriptor.ImplementationInstance is not null)
            {
                var name = descriptor.ImplementationInstance.GetType().FullName ?? string.Empty;
                Assert.DoesNotContain("Fake", name, StringComparison.Ordinal);
                Assert.DoesNotContain(".Fakes.", name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Session_and_main_view_model_are_singletons_settings_are_transient()
    {
        var services = new ServiceCollection();
        services.AddImageTaggerServices();
        services.AddSingleton<IPlatformService>(_ => new HeadlessPlatformService());
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<ITagInferenceService>(),
            provider.GetRequiredService<ITagInferenceService>());
        Assert.Same(
            provider.GetRequiredService<IMemoryCache>(),
            provider.GetRequiredService<IMemoryCache>());
        Assert.Same(
            provider.GetRequiredService<MainViewModel>(),
            provider.GetRequiredService<MainViewModel>());
        Assert.NotSame(
            provider.GetRequiredService<SettingsViewModel>(),
            provider.GetRequiredService<SettingsViewModel>());
    }

    /// <summary>生产占位平台服务（headless 可解析，不依赖窗口）。</summary>
    private sealed class HeadlessPlatformService : IPlatformService
    {
        public Task<IReadOnlyList<string>?> PickImageFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>?>(null);

        public Task<string?> PickFolderAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> RevealInFileManagerAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
