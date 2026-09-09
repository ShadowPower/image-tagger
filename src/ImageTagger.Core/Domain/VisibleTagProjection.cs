namespace ImageTagger.Core.Domain;

/// <summary>A visible probability paired with the one shared catalog entry; tag text is never copied.</summary>
public sealed record VisibleTag(TagCatalogEntry CatalogEntry, float Probability)
{
    public int Index => CatalogEntry.Index;
}

/// <summary>Creates only threshold-visible tag objects and applies deterministic model-index tie breaking.</summary>
public static class VisibleTagProjection
{
    public static IReadOnlyList<VisibleTag> Project(
        TagCatalog catalog,
        ReadOnlySpan<float> probabilities,
        double threshold,
        string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (probabilities.Length != catalog.Count)
            throw new ArgumentException("Probability count must match the shared TagCatalog.", nameof(probabilities));
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        var visible = new List<VisibleTag>();
        for (var index = 0; index < probabilities.Length; index++)
        {
            var probability = probabilities[index];
            if (!float.IsFinite(probability) || probability < threshold)
                continue;
            var entry = catalog[index];
            if (groupId is not null && !string.Equals(entry.DisplayGroup, groupId, StringComparison.Ordinal))
                continue;
            visible.Add(new VisibleTag(entry, probability));
        }

        visible.Sort(static (left, right) =>
        {
            var confidence = right.Probability.CompareTo(left.Probability);
            return confidence != 0 ? confidence : left.Index.CompareTo(right.Index);
        });
        return visible;
    }

    public static IReadOnlyList<VisibleTag> Project(
        TagCatalog catalog,
        PredictionSnapshot prediction,
        double threshold,
        string? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        return Project(catalog, prediction.Probabilities, threshold, groupId);
    }
}
