using System.Reflection;
using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using Xunit;

namespace ImageTagger.Tests.Contracts;

/// <summary>P-06 acceptance: Core must compile without UI, platform or ONNX dependencies.</summary>
public class CoreDependencyTests
{
    [Fact]
    public void Core_references_no_ui_platform_or_onnx_assemblies()
    {
        var coreAssembly = typeof(TaggerException).Assembly;
        var referenced = coreAssembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToHashSet();

        string[] forbidden =
        [
            "Avalonia.Base", "Avalonia.Controls", "Avalonia.ShadUI", "ShadUI",
            "Microsoft.ML.OnnxRuntime", "Microsoft.WindowsAppSDK.ML",
            "SixLabors.ImageSharp", "CsvHelper",
            "Microsoft.Extensions.DependencyInjection",
        ];

        var violations = forbidden.Where(referenced.Contains).ToList();
        Assert.True(violations.Count == 0,
            $"Core must not reference UI/platform/ONNX/parsing assemblies, found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void PredictionSnapshot_keeps_only_probability_array()
    {
        var snapshot = new PredictionSnapshot(
            "fp", DateTimeOffset.Now, TimeSpan.Zero, "rt", "cpu", 1, new float[6]);

        // Only metadata + one float[]; no TagCatalogEntry objects are reachable from the snapshot.
        var fields = new Queue<Type>();
        fields.Enqueue(snapshot.GetType());
        var visited = new HashSet<string>();
        var foundCatalog = false;
        while (fields.Count > 0)
        {
            var type = fields.Dequeue();
            if (!visited.Add(type.FullName ?? type.Name)) continue;
            if (type == typeof(TagCatalogEntry))
            {
                foundCatalog = true;
                break;
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (field.FieldType == typeof(TagCatalogEntry))
                    foundCatalog = true;
        }
        Assert.False(foundCatalog, "PredictionSnapshot must not embed TagCatalogEntry objects");
        Assert.Equal(6, snapshot.Probabilities.Length);
    }

    [Fact]
    public void FakeModelPack_descriptor_has_two_groups_and_six_labels()
    {
        var descriptor = FakeModelPack.Descriptor();
        Assert.Equal(2, descriptor.Groups.Length);
        Assert.Equal(6, descriptor.Output.LabelCount);
        Assert.Equal("fake-micro-tagger", descriptor.Id);
        // Two mock groups prove the generic layer is not bound to WD group names.
        Assert.DoesNotContain(descriptor.Groups, g => g.Id == "general");
    }
}
