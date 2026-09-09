using ImageTagger.Core.Domain;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class VisibleTagProjectionTests
{
    [Fact]
    public void Projects_shared_entries_with_correct_text_translation_and_groups()
    {
        var entries = new[]
        {
            new TagCatalogEntry(0, "rating_safe", "安全", "rating"),
            new TagCatalogEntry(1, "long_hair", "长发", "general"),
            new TagCatalogEntry(2, "solo", null, "general"),
        };
        var catalog = new TagCatalog(entries);

        var projected = VisibleTagProjection.Project(catalog, [0.4f, 0.9f, 0.8f], 0.5, "general");

        Assert.Equal([1, 2], projected.Select(tag => tag.Index));
        Assert.Same(catalog[1], projected[0].CatalogEntry);
        Assert.Equal("long_hair", projected[0].CatalogEntry.OriginalName);
        Assert.Equal("长发", projected[0].CatalogEntry.ChineseTranslation);
        Assert.Equal("general", projected[0].CatalogEntry.DisplayGroup);
    }

    [Fact]
    public void Confidence_ties_are_stable_by_model_output_index_and_boundary_is_inclusive()
    {
        var catalog = Catalog(5);

        var projected = VisibleTagProjection.Project(catalog, [0.5f, 0.8f, 0.8f, 0.2f, 0.8f], 0.5);

        Assert.Equal([1, 2, 4, 0], projected.Select(tag => tag.Index));
    }

    [Fact]
    public void Large_catalog_allocates_objects_only_for_visible_results()
    {
        const int count = 16_473;
        var catalog = Catalog(count);
        var probabilities = new float[count];
        probabilities[10] = 0.91f;
        probabilities[16_000] = 0.92f;

        var projected = VisibleTagProjection.Project(catalog, probabilities, 0.9);

        Assert.Equal(2, projected.Count);
        Assert.Equal([16_000, 10], projected.Select(tag => tag.Index));
        Assert.Same(catalog[16_000], projected[0].CatalogEntry);
    }

    [Fact]
    public void Rejects_probability_catalog_mismatch_and_ignores_non_finite_values()
    {
        var catalog = Catalog(3);
        Assert.Throws<ArgumentException>(() => VisibleTagProjection.Project(catalog, [0.1f], 0.5));

        var projected = VisibleTagProjection.Project(catalog, [float.NaN, float.PositiveInfinity, 0.7f], 0.5);
        Assert.Equal(2, Assert.Single(projected).Index);
    }

    private static TagCatalog Catalog(int count) => new(
        Enumerable.Range(0, count)
            .Select(index => new TagCatalogEntry(index, $"tag_{index}", null, "general"))
            .ToArray());
}
