using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace ImageTagger.Tests.Contracts;

/// <summary>
/// P-06 acceptance: the Model Pack JSON Schema must reject unknown fields,
/// unknown versions, illegal paths, illegal step parameters and invalid
/// input/output contracts — and accept the WD Canary manifest shape.
/// </summary>
public class ModelPackSchemaTests
{
    private static readonly JsonSchema Schema = LoadEmbeddedSchema();

    private readonly ITestOutputHelper _output;

    public ModelPackSchemaTests(ITestOutputHelper output) => _output = output;

    private static JsonSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(ImageTagger.Core.ModelPacks.ModelDescriptor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("model-pack.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("schema resource not found");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry(),
        });
    }

    private static JsonNode ValidManifest()
    {
        return JsonNode.Parse("""
            {
              "schemaVersion": 1,
              "id": "test-pack",
              "displayName": "Test Pack",
              "task": "multi-label-image-tagging",
              "model": "model.onnx",
              "groups": [
                { "id": "subject", "name": "Subject", "displayName": "主体" }
              ],
              "input": { "name": "images", "layout": "NCHW", "dtype": "float32", "batch": "dynamic", "width": 64, "height": 64 },
              "preprocessing": {
                "schemaVersion": 1,
                "steps": [
                  { "op": "decode", "version": 1, "frame": "first", "colorManagement": "ignore" },
                  { "op": "resize", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bicubic-v1" }
                ]
              },
              "output": { "name": "logits", "activation": "sigmoid", "labelCount": 6 },
              "catalog": "tags.csv",
              "defaultThreshold": 0.5
            }
            """)!;
    }

    private static IEnumerable<string> CollectErrors(Json.Schema.EvaluationResults result)
    {
        if (result.Errors is { Count: > 0 })
            foreach (var error in result.Errors.Values)
                yield return $"{result.InstanceLocation}: {error}";
        if (result.Details is { Count: > 0 })
            foreach (var child in result.Details)
                foreach (var nested in CollectErrors(child))
                    yield return nested;
    }

    private void AssertValid(JsonNode node)
    {
        var result = Schema.Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        });
        if (!result.IsValid)
            _output.WriteLine(string.Join("\n", CollectErrors(result)));
        Assert.True(result.IsValid, "manifest should validate");
    }

    private void AssertInvalid(JsonNode node, string expectFragment)
    {
        var result = Schema.Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        });
        Assert.True(!result.IsValid, $"manifest should be rejected ({expectFragment})");
        var messages = string.Join("\n", CollectErrors(result));
        _output.WriteLine(messages);
        Assert.True(messages.Length > 0);
    }

    [Fact]
    public void Accepts_wd_canary_shaped_manifest() => AssertValid(ValidManifest());

    [Fact]
    public void Rejects_unknown_top_level_field()
    {
        var node = ValidManifest();
        node["customScript"] = "evil.js";
        AssertInvalid(node, "unknown top-level field");
    }

    [Fact]
    public void Rejects_unknown_schema_version()
    {
        var node = ValidManifest();
        node["schemaVersion"] = 2;
        AssertInvalid(node, "unknown schemaVersion");
    }

    [Fact]
    public void Rejects_illegal_model_path()
    {
        var node = ValidManifest();
        node["model"] = "../model.onnx";
        AssertInvalid(node, "path traversal in model");
    }

    [Fact]
    public void Rejects_subdirectory_model_path()
    {
        var node = ValidManifest();
        node["catalog"] = "sub/tags.csv";
        AssertInvalid(node, "subdirectory path");
    }

    [Fact]
    public void Rejects_unknown_operator()
    {
        var node = ValidManifest();
        var unknownOpSteps = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(node["preprocessing"])["steps"]);
        unknownOpSteps[0] = JsonNode.Parse(
            """{ "op": "run-script", "version": 1, "script": "x.sh" }""");
        AssertInvalid(node, "unknown op");
    }

    [Fact]
    public void Rejects_illegal_step_parameter_for_op()
    {
        var node = ValidManifest();
        var illegalParamSteps = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(node["preprocessing"])["steps"]);
        illegalParamSteps[0] = JsonNode.Parse(
            """{ "op": "decode", "version": 1, "frame": "first", "colorManagement": "ignore", "gamma": 2.2 }""");
        AssertInvalid(node, "parameter not in closed op schema");
    }

    [Fact]
    public void Rejects_missing_required_step_parameter()
    {
        var node = ValidManifest();
        var missingParamSteps = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(node["preprocessing"])["steps"]);
        missingParamSteps[1] = JsonNode.Parse(
            """{ "op": "resize", "version": 1, "width": 64 }""");
        AssertInvalid(node, "missing resize parameters");
    }

    [Fact]
    public void Rejects_invalid_input_contract()
    {
        var node = ValidManifest();
        Assert.IsType<JsonObject>(node["input"])["layout"] = "CHWN";
        AssertInvalid(node, "unsupported layout");
    }

    [Fact]
    public void Rejects_invalid_output_activation()
    {
        var node = ValidManifest();
        Assert.IsType<JsonObject>(node["output"])["activation"] = "softmax";
        AssertInvalid(node, "unsupported activation");
    }

    [Fact]
    public void Rejects_unknown_group_id_characters()
    {
        var node = ValidManifest();
        var groups = Assert.IsType<JsonArray>(node["groups"]);
        Assert.IsType<JsonObject>(groups[0])["id"] = "General Group";
        AssertInvalid(node, "group id with space");
    }

    [Fact]
    public void Rejects_threshold_out_of_range()
    {
        var node = ValidManifest();
        node["defaultThreshold"] = 1.5;
        AssertInvalid(node, "threshold out of range");
    }

    [Fact]
    public void Accepts_the_declared_center_crop_operator()
    {
        var node = ValidManifest();
        node["preprocessing"]!["steps"]![1] = JsonNode.Parse(
            """{ "op": "crop", "version": 1, "width": 32, "height": 32, "anchor": "center" }""");
        AssertValid(node);
    }
}
