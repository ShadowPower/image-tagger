using Avalonia.Controls;
using ImageTagger.App.Services;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Imaging;
using ImageTagger.Infrastructure.Logging;
using ImageTagger.Infrastructure.Metadata;
using ImageTagger.Infrastructure.ModelPacks;
using ImageTagger.Infrastructure.Platform;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Infrastructure.Runtime;
using ImageTagger.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace ImageTagger.App.Composition;

/// <summary>
/// U-01 首步 Composition Root（DESIGN 13.3）。
/// 使用 Microsoft.Extensions.DependencyInjection 组装单例 Session、ModelPackService、
/// 缓存与 ViewModel 生命周期；依赖全部经构造函数注入，不使用 Service Locator。
/// 生产路径不用 fake/design service；DesignData 仅在 XAML 设计时使用。
/// 解析只在 Program/App.axaml.cs 执行一次。
/// </summary>
public static class ServiceRegistration
{
    /// <summary>缩略图缓存上限：256 张（DESIGN 6.4）。</summary>
    public const int ThumbnailCacheCapacity = 256;

    /// <summary>会话推理结果缓存上限（条目数）。</summary>
    public const int SessionCacheCapacity = 256;

    /// <summary>
    /// 注册全部生产服务（除窗口绑定的 <see cref="IPlatformService"/> 外）。
    /// <see cref="IPlatformService"/> 由 <see cref="AddDesktopPlatformService"/> 在主窗口创建后注册。
    /// </summary>
    public static IServiceCollection AddImageTaggerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAppResourceLocator, AppResourceLocator>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IPromptSettingsStore, PromptSettingsStore>();

        services.AddSingleton<ModelPackReader>();
        // 显式使用标准算子注册表：MS.DI 会把可选的 IEnumerable 依赖解析为空集合，
        // 类型注册会绕过默认构造语义导致注册表为空，此处必须明确构造。
        services.AddSingleton<IPreprocessingPipelineCompiler>(_ => new PreprocessingPipelineCompiler());
        services.AddSingleton<IModelPackService, ModelPackService>();
        services.AddSingleton<IPreprocessingExecutor, StandardPreprocessingExecutor>();
        services.AddSingleton<IGenerationInfoParser, GenerationInfoParser>();
        services.AddSingleton<ITaggerModelAdapter>(provider => (ITaggerModelAdapter)provider.GetRequiredService<ITagInferenceService>());

        services.AddSingleton<IInferenceRuntimeFactory>(provider =>
        {
            string ResolveModelPath(ModelDescriptor descriptor)
            {
                return descriptor.ModelFile;
            }

            var (cpu, windows, coreMl) = RuntimeFactorySelector.CreateDefault(ResolveModelPath);
            return new AdaptiveInferenceRuntimeFactory(cpu, windows, coreMl);
        });
        services.AddSingleton<ITagInferenceService>(provider => new TagInferenceService(
            provider.GetRequiredService<IInferenceRuntimeFactory>(),
            provider.GetRequiredService<IPreprocessingExecutor>(),
            settings: provider.GetRequiredService<ISettingsStore>()));

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = SessionCacheCapacity,
            CompactionPercentage = 0.25,
        }));

        services.AddSingleton<IThumbnailService>(_ => new ThumbnailService(
            ThumbnailService.DefaultMaximumEdge,
            ThumbnailCacheCapacity));
        services.AddSingleton<IImageImportService>(_ => new ImageImportService());

        services.AddSingleton<ILogService>(provider =>
            LoggingSetup.CreateLogService(provider.GetRequiredService<IAppResourceLocator>().LogsRoot));
        services.AddSingleton<IUserNotificationService, UserNotificationService>();

        services.AddSingleton<MainViewModel>(provider => new MainViewModel(
            provider.GetService<ThemeWatcher>(),
            provider.GetRequiredService<IThumbnailService>(),
            provider.GetRequiredService<IImageImportService>(),
            provider.GetService<IPlatformService>(),
            tags: provider.GetRequiredService<TagsViewModel>(),
            metadata: new MetadataViewModel(provider.GetRequiredService<IGenerationInfoParser>()),
            prompt: provider.GetRequiredService<PromptBuilderViewModel>(),
            promptStore: provider.GetRequiredService<IPromptSettingsStore>(),
            notifications: provider.GetRequiredService<IUserNotificationService>()));
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<TagsViewModel>();
        services.AddTransient<MetadataViewModel>(provider =>
            new MetadataViewModel(provider.GetRequiredService<IGenerationInfoParser>()));
        services.AddTransient<PromptBuilderViewModel>();

        return services;
    }

    /// <summary>在主窗口创建后注册窗口绑定的桌面平台服务（文件选择/剪贴板/启动）。</summary>
    public static IServiceCollection AddDesktopPlatformService(this IServiceCollection services, TopLevel owner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(owner);
        services.AddSingleton<IPlatformService>(_ => new DesktopPlatformService(owner));
        return services;
    }
}
