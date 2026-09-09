using ImageTagger.Core.Domain;
using ImageTagger.Core;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>Routes each session creation using the latest user preference.</summary>
public sealed class AdaptiveInferenceRuntimeFactory : IInferenceRuntimeFactory
{
    private readonly IInferenceRuntimeFactory _cpu;
    private readonly IInferenceRuntimeFactory _windows;
    private readonly IInferenceRuntimeFactory _coreMl;

    public AdaptiveInferenceRuntimeFactory(
        IInferenceRuntimeFactory cpu,
        IInferenceRuntimeFactory windows,
        IInferenceRuntimeFactory coreMl)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _coreMl = coreMl ?? throw new ArgumentNullException(nameof(coreMl));
    }

    public Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor,
        AccelerationPreference preference,
        CancellationToken cancellationToken)
    {
        var selected = RuntimeFactorySelector.Select(preference, _cpu, _windows, _coreMl);
        return selected.CreateAsync(descriptor, preference, cancellationToken);
    }
}
