using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.ModelPacks;

/// <summary>Validates and loads the one external Model Pack directory chosen by the caller.</summary>
public sealed class ModelPackService : IModelPackService
{
    private readonly ModelPackReader _reader;
    private readonly IPreprocessingPipelineCompiler _compiler;

    public ModelPackService(ModelPackReader? reader = null, IPreprocessingPipelineCompiler? compiler = null)
    {
        _reader = reader ?? new ModelPackReader();
        _compiler = compiler ?? new Preprocessing.PreprocessingPipelineCompiler();
    }

    public ModelDescriptor ReadDescriptorFromPath(string modelPackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPackPath);
        return _reader.ReadDescriptor(Path.GetFullPath(modelPackPath));
    }

    public async Task<LoadedModelPack> LoadFromPathAsync(string modelPackPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPackPath);
        var root = Path.GetFullPath(modelPackPath);
        var validated = await _reader.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        var perSample = new ModelInputTensor
        {
            DType = TensorDType.Float32,
            Shape = string.Equals(validated.Descriptor.Input.Layout, "NHWC", StringComparison.Ordinal)
                ? [validated.Descriptor.Input.Height, validated.Descriptor.Input.Width, 3]
                : [3, validated.Descriptor.Input.Height, validated.Descriptor.Input.Width],
            Layout = validated.Descriptor.Input.Layout,
        };
        var pipeline = _compiler.ValidateAndCompile(validated.Descriptor.Preprocessing, perSample);
        var descriptor = validated.Descriptor with
        {
            ModelFile = Path.Combine(root, validated.Descriptor.ModelFile),
        };
        return new LoadedModelPack(descriptor, validated.Catalog, validated.Fingerprint, pipeline);
    }
}
