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
using Microsoft.Extensions.DependencyInjection;

internal static class CliComposition
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppResourceLocator, AppResourceLocator>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ModelPackReader>();
        services.AddSingleton<IPreprocessingPipelineCompiler>(_ => new PreprocessingPipelineCompiler());
        services.AddSingleton<IModelPackService, ModelPackService>();
        services.AddSingleton<IPreprocessingExecutor, StandardPreprocessingExecutor>();
        services.AddSingleton<IImageImportService, ImageImportService>();
        services.AddSingleton<IGenerationInfoParser, GenerationInfoParser>();
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
        services.AddSingleton<ILogService>(provider => LoggingSetup.CreateLogService(provider.GetRequiredService<IAppResourceLocator>().LogsRoot));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
