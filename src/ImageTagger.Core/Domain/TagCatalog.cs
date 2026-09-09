namespace ImageTagger.Core.Domain;

/// <summary>
/// One catalog row, shared globally per Model Pack. Loaded once; image results
/// reference entries by index, never by copying names or translations.
/// </summary>
/// <param name="Index">Model output index, contiguous from 0.</param>
/// <param name="OriginalName">Untouched original tag.</param>
/// <param name="ChineseTranslation">Chinese translation; null when identical to the original tag.</param>
/// <param name="DisplayGroup">Group id declared by the Model Pack manifest (e.g. "general").</param>
public sealed record TagCatalogEntry(
    int Index,
    string OriginalName,
    string? ChineseTranslation,
    string DisplayGroup);

/// <summary>Shared, read-only tag table for a loaded Model Pack.</summary>
public sealed class TagCatalog
{
    private readonly TagCatalogEntry[] _entries;
    private readonly Dictionary<string, int> _indexByName;

    public TagCatalog(IReadOnlyList<TagCatalogEntry> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("tag catalog is empty", nameof(entries));
        _entries = [.. entries];
        _indexByName = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Index != i)
                throw new ArgumentException($"tag index must be contiguous from 0 (got {_entries[i].Index} at position {i})");
            if (!_indexByName.TryAdd(_entries[i].OriginalName, i))
                throw new ArgumentException($"duplicate tag name '{_entries[i].OriginalName}'");
        }
    }

    public int Count => _entries.Length;

    public TagCatalogEntry this[int index] => _entries[index];

    public IReadOnlyList<TagCatalogEntry> Entries => _entries;

    public bool TryGetIndex(string name, out int index) => _indexByName.TryGetValue(name, out index);
}

/// <summary>
/// Per-image user override of model decisions: indices the user unchecked even
/// though the probability is above threshold. Prompt building removes them.
/// </summary>
public sealed class TagSelection
{
    /// <summary>Indices forced out of the prompt regardless of probability.</summary>
    public HashSet<int> ForceExcludedIndices { get; } = new();
}
