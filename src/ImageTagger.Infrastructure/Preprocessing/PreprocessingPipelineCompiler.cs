using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageTagger.Core;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;

namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>Validates a linear typed pipeline and caches immutable compiled plans by normalized fingerprint.</summary>
public sealed class PreprocessingPipelineCompiler : IPreprocessingPipelineCompiler
{
    private const int ImplementationVersion = 1;
    private readonly IReadOnlyDictionary<string, IPreprocessOperatorFactory> _factories;
    private readonly ConcurrentDictionary<string, Lazy<CompiledPreprocessingPipeline>> _cache =
        new(StringComparer.Ordinal);

    public PreprocessingPipelineCompiler(IEnumerable<IPreprocessOperatorFactory>? factories = null)
    {
        // 空集合与 null 等价，均回落标准注册表：DI 容器会把可选集合依赖解析
        // 为空枚举而非 null；空注册表无法编译任何流水线，永远不是有效显式选择。
        if (factories is null || !factories.Any())
        {
            _factories = StandardPreprocessOperatorRegistry.Create();
            return;
        }

        try
        {
            _factories = factories.ToDictionary(factory => factory.Op, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Preprocessing factory names must be unique.", nameof(factories), exception);
        }
    }

    public CompiledPreprocessingPipeline ValidateAndCompile(
        PreprocessingPipelineDescriptor descriptor,
        ModelInputTensor modelInput)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(modelInput);
        if (descriptor.SchemaVersion != 1)
            Invalid($"unsupported preprocessing schemaVersion {descriptor.SchemaVersion}");
        if (descriptor.OrderedSteps is not { Length: > 0 and <= 32 })
            Invalid("pipeline must contain 1 to 32 steps");
        if (descriptor.OrderedSteps[0].Op != "decode"
            || descriptor.OrderedSteps.Count(step => step.Op == "decode") != 1)
            Invalid("decode must occur exactly once as the first step");
        if (modelInput.DType != TensorDType.Float32
            || modelInput.Layout is not ("NCHW" or "NHWC")
            || modelInput.Shape.Length != 3)
            Invalid("model input must be a per-sample float32 NCHW or NHWC [3,H,W]/[H,W,3] tensor");

        var validated = ValidateTypeAndShapeChain(descriptor, modelInput);
        var fingerprint = CreateFingerprint(descriptor, modelInput, validated.Select(item => item.Factory));
        var lazy = _cache.GetOrAdd(
            fingerprint,
            _ => new Lazy<CompiledPreprocessingPipeline>(
                () => Compile(fingerprint, modelInput, validated),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<string, Lazy<CompiledPreprocessingPipeline>>(fingerprint, lazy));
            throw;
        }
    }

    private List<ValidatedStep> ValidateTypeAndShapeChain(
        PreprocessingPipelineDescriptor descriptor,
        ModelInputTensor modelInput)
    {
        var currentKind = TensorKind.EncodedImage;
        var shape = (Width: 0, Height: 0, Channels: 0);
        int[] axisOrder = [0, 1, 2]; // logical H,W,C axes in current memory order
        var validated = new List<ValidatedStep>(descriptor.OrderedSteps.Length);

        foreach (var step in descriptor.OrderedSteps)
        {
            if (!_factories.TryGetValue(step.Op, out var factory))
                Invalid($"unknown preprocessing operator '{step.Op}'");
            if (!factory.SupportedVersions.Contains(step.Version))
                Invalid($"operator '{step.Op}' does not support version {step.Version}");
            factory.ValidateParameters(step.Parameters);
            var kinds = factory.GetKinds(step.Parameters);
            if (kinds.Input != currentKind)
                Invalid($"operator '{step.Op}' expects {kinds.Input} but received {currentKind}");

            shape = factory.InferShape(shape, step.Parameters);
            if (shape.Width < 0 || shape.Height < 0 || shape.Channels < 0 || shape.Channels > 4)
                Invalid($"operator '{step.Op}' produced an invalid shape");
            currentKind = kinds.Output;
            if (step.Op == "permute")
            {
                var order = GetPermutation(step.Parameters["order"]);
                axisOrder = order.Select(index => axisOrder[index]).ToArray();
            }
            validated.Add(new ValidatedStep(step, factory));
        }

        if (currentKind != TensorKind.FloatTensor)
            Invalid("pipeline must produce a final FloatTensor");
        if (shape.Width <= 0 || shape.Height <= 0 || shape.Channels <= 0)
            Invalid("pipeline final tensor shape is not statically known");

        int[] logicalShape = [shape.Height, shape.Width, shape.Channels];
        var actualShape = axisOrder.Select(index => logicalShape[index]).ToArray();
        if (!actualShape.SequenceEqual(modelInput.Shape))
        {
            Invalid(
                $"pipeline tensor [{string.Join(',', actualShape)}] does not match model input " +
                $"[{string.Join(',', modelInput.Shape)}]");
        }
        try
        {
            _ = checked(shape.Width * shape.Height * shape.Channels * sizeof(float));
        }
        catch (OverflowException)
        {
            Invalid("pipeline tensor byte size overflows the supported range");
        }
        return validated;
    }

    private static CompiledPreprocessingPipeline Compile(
        string fingerprint,
        ModelInputTensor input,
        IReadOnlyList<ValidatedStep> validated) => new()
    {
        Fingerprint = fingerprint,
        PerSampleTensorContract = input,
        Steps = validated.Select(item => item.Step).ToArray(),
        Operators = validated.Select(item => item.Factory.Compile(item.Step.Parameters)).ToArray(),
    };

    private static string CreateFingerprint(
        PreprocessingPipelineDescriptor descriptor,
        ModelInputTensor input,
        IEnumerable<IPreprocessOperatorFactory> factories)
    {
        var builder = new StringBuilder();
        builder.Append("pipeline-schema=").Append(descriptor.SchemaVersion)
            .Append(";implementation=").Append(ImplementationVersion)
            .Append(";imagesharp=")
            .Append(typeof(SixLabors.ImageSharp.Image).Assembly.GetName().Version)
            .Append(";input=").Append(input.DType).Append(':').Append(input.Layout).Append(':')
            .AppendJoin(',', input.Shape).AppendLine();

        var index = 0;
        foreach (var pair in descriptor.OrderedSteps.Zip(factories))
        {
            builder.Append(index++).Append(':').Append(pair.First.Op).Append('@').Append(pair.First.Version)
                .Append(':').Append(pair.Second.GetType().FullName).Append('{');
            foreach (var parameter in pair.First.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                builder.Append(parameter.Key).Append('=');
                AppendNormalized(builder, parameter.Value);
                builder.Append(';');
            }
            builder.AppendLine("}");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendNormalized(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                builder.Append('"').Append(text.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;
            case System.Collections.IEnumerable sequence:
                builder.Append('[');
                foreach (var item in sequence.Cast<object?>())
                {
                    AppendNormalized(builder, item);
                    builder.Append(',');
                }
                builder.Append(']');
                break;
            case IFormattable number:
                builder.Append(number.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                Invalid($"unsupported parameter value type {value.GetType().Name}");
                break;
        }
    }

    private static int[] GetPermutation(object? value)
    {
        if (value is string)
            Invalid("permute order is not an array");
        if (value is not System.Collections.IEnumerable sequence)
            throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "permute order is not an array");
        return sequence.Cast<object?>().Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray();
    }

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new TaggerException(TaggerErrorCode.ModelPackInvalid, message);

    private sealed record ValidatedStep(
        PreprocessStepDescriptor Step,
        IPreprocessOperatorFactory Factory);
}
