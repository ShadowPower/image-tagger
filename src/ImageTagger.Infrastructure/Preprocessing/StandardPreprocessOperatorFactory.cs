using ImageTagger.Core;
using ImageTagger.Core.Pipelines;

namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Closed metadata/validation factory for one of the twelve v1 standard
/// operators. Pixel execution is supplied by the B-05 execution plan.
/// </summary>
public sealed class StandardPreprocessOperatorFactory : IPreprocessOperatorFactory
{
    private static readonly IReadOnlySet<int> VersionOne = new HashSet<int> { 1 };

    public StandardPreprocessOperatorFactory(string op)
    {
        if (!AllOperatorNames.Contains(op, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown standard operator '{op}'.", nameof(op));
        Op = op;
    }

    public static IReadOnlyList<string> AllOperatorNames { get; } =
    [
        "decode", "exif-transpose", "ensure-color", "alpha-composite",
        "pad-to-square", "resize", "crop", "reorder-channels", "cast",
        "divide", "normalize", "permute",
    ];

    public string Op { get; }

    public IReadOnlySet<int> SupportedVersions => VersionOne;

    public void ValidateParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        switch (Op)
        {
            case "decode":
                RequireKeys(parameters, "frame", "colorManagement");
                RequireString(parameters, "frame", "first");
                RequireString(parameters, "colorManagement", "ignore");
                break;
            case "exif-transpose":
                RequireKeys(parameters);
                break;
            case "ensure-color":
                RequireKeys(parameters, "mode");
                RequireString(parameters, "mode", "rgb-or-rgba-if-transparent", "rgb");
                break;
            case "alpha-composite":
                RequireKeys(parameters, "when", "background");
                RequireString(parameters, "when", "has-alpha");
                RequireByteTriplet(parameters, "background");
                break;
            case "pad-to-square":
                RequireKeys(parameters, "anchor", "background");
                RequireString(parameters, "anchor", "floor-center");
                RequireByteTriplet(parameters, "background");
                break;
            case "resize":
                RequireKeys(parameters, "width", "height", "sampler");
                RequireDimension(parameters, "width");
                RequireDimension(parameters, "height");
                RequireString(parameters, "sampler", "pillow-bicubic-v1", "imagesharp-bicubic-v1");
                break;
            case "crop":
                RequireKeys(parameters, "width", "height", "anchor");
                RequireDimension(parameters, "width");
                RequireDimension(parameters, "height");
                RequireString(parameters, "anchor", "center");
                break;
            case "reorder-channels":
            case "permute":
                RequireKeys(parameters, "order");
                RequirePermutation(parameters, "order");
                break;
            case "cast":
                RequireKeys(parameters, "dtype");
                RequireString(parameters, "dtype", "float32");
                break;
            case "divide":
                RequireKeys(parameters, "value");
                var divisor = RequireNumber(parameters, "value");
                if (!double.IsFinite(divisor) || divisor <= 0)
                    Invalid("value must be finite and greater than zero");
                break;
            case "normalize":
                RequireKeys(parameters, "mean", "std");
                var means = RequireNumberTriplet(parameters, "mean");
                var standardDeviations = RequireNumberTriplet(parameters, "std");
                if (means.Any(value => !double.IsFinite(value)))
                    Invalid("mean values must be finite");
                if (standardDeviations.Any(value => !double.IsFinite(value) || value <= 0))
                    Invalid("std values must be finite and greater than zero");
                break;
            default:
                Invalid("operator is not registered");
                break;
        }
    }

    public (TensorKind Input, TensorKind Output) GetKinds(IReadOnlyDictionary<string, object?> parameters)
    {
        ValidateParameters(parameters);
        return Op switch
        {
            "decode" => (TensorKind.EncodedImage, TensorKind.Image),
            "exif-transpose" => (TensorKind.Image, TensorKind.Image),
            "ensure-color" when (string)parameters["mode"]! == "rgb" =>
                (TensorKind.Image, TensorKind.RgbImage),
            "ensure-color" => (TensorKind.Image, TensorKind.Image),
            "alpha-composite" => (TensorKind.Image, TensorKind.RgbImage),
            "pad-to-square" or "resize" or "crop" or "reorder-channels" =>
                (TensorKind.RgbImage, TensorKind.RgbImage),
            "cast" => (TensorKind.RgbImage, TensorKind.FloatTensor),
            "divide" or "normalize" or "permute" =>
                (TensorKind.FloatTensor, TensorKind.FloatTensor),
            _ => throw CreateInvalid("operator kind is not declared"),
        };
    }

    public (int Width, int Height, int Channels) InferShape(
        (int Width, int Height, int Channels) input,
        IReadOnlyDictionary<string, object?> parameters)
    {
        ValidateParameters(parameters);
        return Op switch
        {
            "decode" => (0, 0, 4),
            "ensure-color" when (string)parameters["mode"]! == "rgb" =>
                (input.Width, input.Height, 3),
            "ensure-color" => (input.Width, input.Height, 4),
            "alpha-composite" => (input.Width, input.Height, 3),
            "pad-to-square" =>
                (Math.Max(input.Width, input.Height), Math.Max(input.Width, input.Height), input.Channels),
            "resize" => (GetInt(parameters, "width"), GetInt(parameters, "height"), input.Channels),
            "crop" => InferCrop(input, parameters),
            "reorder-channels" => (input.Width, input.Height, 3),
            _ => input,
        };
    }

    public IPreprocessOperator Compile(IReadOnlyDictionary<string, object?> parameters)
    {
        ValidateParameters(parameters);
        return new DeclaredOperator(Op);
    }

    private static (int Width, int Height, int Channels) InferCrop(
        (int Width, int Height, int Channels) input,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var width = GetInt(parameters, "width");
        var height = GetInt(parameters, "height");
        if ((input.Width > 0 && width > input.Width) || (input.Height > 0 && height > input.Height))
            throw CreateInvalid("crop dimensions exceed the input image");
        return (width, height, input.Channels);
    }

    private void RequireKeys(IReadOnlyDictionary<string, object?> parameters, params string[] expected)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var unknown = parameters.Keys.Where(key => !expectedSet.Contains(key)).ToArray();
        var missing = expected.Where(key => !parameters.ContainsKey(key)).ToArray();
        if (unknown.Length > 0)
            Invalid($"unknown parameter(s): {string.Join(", ", unknown)}");
        if (missing.Length > 0)
            Invalid($"missing parameter(s): {string.Join(", ", missing)}");
    }

    private void RequireString(
        IReadOnlyDictionary<string, object?> parameters,
        string name,
        params string[] allowed)
    {
        if (parameters[name] is not string value || !allowed.Contains(value, StringComparer.Ordinal))
            Invalid($"{name} must be one of: {string.Join(", ", allowed)}");
    }

    private void RequireDimension(IReadOnlyDictionary<string, object?> parameters, string name)
    {
        var value = RequireNumber(parameters, name);
        if (value != Math.Truncate(value) || value is < 1 or > 8192)
            Invalid($"{name} must be an integer from 1 to 8192");
    }

    private void RequireByteTriplet(IReadOnlyDictionary<string, object?> parameters, string name)
    {
        var values = RequireNumberTriplet(parameters, name);
        if (values.Any(value => value != Math.Truncate(value) || value is < 0 or > 255))
            Invalid($"{name} must contain three bytes");
    }

    private void RequirePermutation(IReadOnlyDictionary<string, object?> parameters, string name)
    {
        var values = RequireNumberTriplet(parameters, name);
        if (!values.Order().SequenceEqual([0d, 1d, 2d]))
            Invalid($"{name} must be a permutation of [0, 1, 2]");
    }

    private double[] RequireNumberTriplet(IReadOnlyDictionary<string, object?> parameters, string name)
    {
        if (parameters[name] is not System.Collections.IEnumerable sequence || parameters[name] is string)
            throw CreateInvalid($"{name} must be an array of three numbers");
        var values = sequence.Cast<object?>().Select(ToNumber).ToArray();
        if (values.Length != 3)
            Invalid($"{name} must contain exactly three values");
        return values;
    }

    private double RequireNumber(IReadOnlyDictionary<string, object?> parameters, string name) =>
        ToNumber(parameters[name]);

    private static double ToNumber(object? value) => value switch
    {
        byte number => number,
        short number => number,
        int number => number,
        long number => number,
        float number => number,
        double number => number,
        decimal number => (double)number,
        _ => throw CreateInvalid("parameter must be numeric"),
    };

    private static int GetInt(IReadOnlyDictionary<string, object?> parameters, string name) =>
        checked((int)ToNumber(parameters[name]));

    private void Invalid(string reason) => throw CreateInvalid($"operator '{Op}': {reason}");

    private static TaggerException CreateInvalid(string message) =>
        new(TaggerErrorCode.ModelPackInvalid, message);

    private sealed class DeclaredOperator(string op) : IPreprocessOperator
    {
        public string Op { get; } = op;
    }
}

/// <summary>Creates the complete immutable v1 standard operator registry.</summary>
public static class StandardPreprocessOperatorRegistry
{
    public static IReadOnlyDictionary<string, IPreprocessOperatorFactory> Create() =>
        StandardPreprocessOperatorFactory.AllOperatorNames.ToDictionary(
            op => op,
            op => (IPreprocessOperatorFactory)new StandardPreprocessOperatorFactory(op),
            StringComparer.Ordinal);
}
