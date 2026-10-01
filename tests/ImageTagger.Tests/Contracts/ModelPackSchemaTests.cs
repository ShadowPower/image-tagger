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
    private static readonly JsonSchema SchemaV1 = LoadEmbeddedSchema("model-pack.schema.json");
    private static readonly JsonSchema SchemaV2 = LoadEmbeddedSchema("model-pack.schema.v2.json");

    private readonly ITestOutputHelper _output;

    public ModelPackSchemaTests(ITestOutputHelper output) => _output = output;

    private static JsonSchema LoadEmbeddedSchema(string fileName)
    {
        var assembly = typeof(ImageTagger.Core.ModelPacks.ModelDescriptor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.Ordinal))
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

    private void AssertValid(JsonNode node, JsonSchema? schema = null)
    {
        var result = (schema ?? SchemaV1).Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        });
        if (!result.IsValid)
            _output.WriteLine(string.Join("\n", CollectErrors(result)));
        Assert.True(result.IsValid, "manifest should validate");
    }

    private void AssertInvalid(JsonNode node, string expectFragment, JsonSchema? schema = null)
    {
        var result = (schema ?? SchemaV1).Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions
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
        node["schemaVersion"] = 3;
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

    /// <summary>v2 manifest: generic geometry operators, bilinear sampler and per-group default thresholds.</summary>
    private static JsonNode ValidManifestV2()
    {
        var node = ValidManifest();
        node["schemaVersion"] = 2;
        node["preprocessing"]!["schemaVersion"] = 2;
        node["groups"]![0]!["defaultThreshold"] = 0.27;
        node["preprocessing"]!["steps"]![1] = JsonNode.Parse(
            """{ "op": "resize-fit", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bilinear-v1", "rounding": "floor" }""");
        Assert.IsType<JsonArray>(node["preprocessing"]!["steps"]).Add(JsonNode.Parse(
            """{ "op": "pad-to-size", "version": 1, "width": 64, "height": 64, "anchor": "floor-center", "background": [0, 0, 0] }"""));
        return node;
    }

    [Fact]
    public void Accepts_v2_manifest_with_generic_geometry_and_group_thresholds() =>
        AssertValid(ValidManifestV2(), SchemaV2);

    [Fact]
    public void V1_schema_rejects_v2_operators_and_group_thresholds()
    {
        var withFit = ValidManifest();
        withFit["preprocessing"]!["steps"]![1] = JsonNode.Parse(
            """{ "op": "resize-fit", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bilinear-v1", "rounding": "floor" }""");
        AssertInvalid(withFit, "v2 operator in v1 pipeline");

        var withBilinear = ValidManifest();
        withBilinear["preprocessing"]!["steps"]![1] = JsonNode.Parse(
            """{ "op": "resize", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bilinear-v1" }""");
        AssertInvalid(withBilinear, "v2 sampler in v1 pipeline");

        var withGroupThreshold = ValidManifest();
        withGroupThreshold["groups"]![0]!["defaultThreshold"] = 0.27;
        AssertInvalid(withGroupThreshold, "group defaultThreshold in v1");
    }

    [Fact]
    public void V2_schema_rejects_missing_rounding_unknown_anchor_and_smaller_targets()
    {
        var missingRounding = ValidManifestV2();
        Assert.IsType<JsonArray>(missingRounding["preprocessing"]!["steps"])[1] = JsonNode.Parse(
            """{ "op": "resize-fit", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bilinear-v1" }""");
        AssertInvalid(missingRounding, "resize-fit missing rounding", SchemaV2);

        var badAnchor = ValidManifestV2();
        Assert.IsType<JsonArray>(badAnchor["preprocessing"]!["steps"])[2] = JsonNode.Parse(
            """{ "op": "pad-to-size", "version": 1, "width": 64, "height": 64, "anchor": "top-left", "background": [0, 0, 0] }""");
        AssertInvalid(badAnchor, "pad-to-size anchor", SchemaV2);
    }
}
