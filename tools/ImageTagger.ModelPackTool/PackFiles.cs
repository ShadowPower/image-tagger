using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace ImageTagger.ModelPackTool;

public static class PackFiles
{
    public const string Manifest = "model.json";
    public const string Model = "model.onnx";
    public const string Catalog = "tags.csv";
    public const string Checksums = "checksums.sha256";
    public const string License = "LICENSE.txt";

    public static readonly string[] All = [Manifest, Model, Catalog, Checksums, License];
}

public static class GroupMap
{
    // Source selected_tags.csv category numbers -> runtime group ids (design 3.6).
    private static readonly Dictionary<int, string> Known = new()
    {
        [9] = "rating",
        [1] = "artist",
        [3] = "copyright",
        [4] = "character",
        [0] = "general",
    };

    public static readonly string[] DisplayOrder =
    [
        "rating", "artist", "copyright", "character", "general",
    ];

    private static readonly Dictionary<string, (string Name, string Display)> Names =
        new()
        {
            ["rating"] = ("Rating", "分级"),
            ["artist"] = ("Artist", "艺术家"),
            ["copyright"] = ("Copyright", "版权"),
            ["character"] = ("Character", "角色"),
            ["general"] = ("General", "通用"),
        };

    public static bool IsKnown(int category) => Known.ContainsKey(category);

    public static string Map(int category) => Known.TryGetValue(category, out var g) ? g : "general";

    public static GroupDescriptor Describe(string id) =>
        Names.TryGetValue(id, out var n)
            ? new GroupDescriptor { Id = id, Name = n.Name, DisplayName = n.Display }
            : new GroupDescriptor { Id = id, Name = id, DisplayName = id };
}

public sealed record SourceTagRow(int Id, string Name, int Category, long Count);

public static class TagMerge
{
    public static List<SourceTagRow> ReadSourceTags(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        });
        csv.Read();
        csv.ReadHeader();
        var rows = new List<SourceTagRow>();
        while (csv.Read())
        {
            rows.Add(new SourceTagRow(
                csv.GetField<int>("tag_id"),
                csv.GetField<string>("name")!,
                csv.GetField<int>("category"),
                csv.GetField<long>("count")));
        }
        return rows;
    }

    public static Dictionary<int, (string Tag, string Translation)> ReadTranslations(string path)
    {
        var result = new Dictionary<int, (string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = System.Text.Json.JsonDocument.Parse(line);
            int id = doc.RootElement.GetProperty("id").GetInt32();
            string tag = doc.RootElement.GetProperty("tag").GetString() ?? "";
            string translation = doc.RootElement.TryGetProperty("translation", out var t)
                ? t.GetString() ?? ""
                : "";
            result[id] = (tag, translation);
        }
        return result;
    }

    /// <summary>Merges category groups and translations into standard catalog rows.</summary>
    public static (List<CatalogRow> Rows, List<string> GroupOrder, BuildInfo Build) Build(
        IReadOnlyList<SourceTagRow> source,
        IReadOnlyDictionary<int, (string Tag, string Translation)> translations,
        string translationSource = "translated_tags_zh.jsonl")
    {
        var groupOrder = new List<string>();
        var categoryCounts = new Dictionary<string, int>();
        var unmapped = new HashSet<int>();
        var rows = new List<CatalogRow>(source.Count);

        foreach (var s in source)
        {
            string group = GroupMap.Map(s.Category);
            if (!GroupMap.IsKnown(s.Category)) unmapped.Add(s.Category);
            if (!groupOrder.Contains(group)) groupOrder.Add(group);
            categoryCounts[$"{s.Category}:{group}"] = categoryCounts.TryGetValue($"{s.Category}:{group}", out var c) ? c + 1 : 1;

            string translation = "";
            if (translations.TryGetValue(s.Id, out var tr))
            {
                if (!string.Equals(tr.Tag, s.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"translation mismatch at id {s.Id}: catalog tag '{s.Name}' vs translation tag '{tr.Tag}'");
                // Identical to original tag means "no translation" (design 8.3).
                translation = string.Equals(tr.Translation, s.Name, StringComparison.Ordinal) ? "" : tr.Translation;
            }
            rows.Add(new CatalogRow(s.Id, s.Name, group, translation, s.Count));
        }

        // Group order follows first appearance in model output order (stable, data-driven).
        var build = new BuildInfo
        {
            TagSource = "selected_tags.csv",
            TranslationSource = translationSource,
            CategoryCounts = categoryCounts,
            UnmappedCategories = unmapped.OrderBy(x => x).Select(x => x.ToString()).ToArray(),
        };
        return (rows, groupOrder, build);
    }
}

public sealed record CatalogRow(int Id, string Name, string Group, string Translation, long Count);

public static class CatalogCsv
{
    public static void Write(string path, IEnumerable<CatalogRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.NewLine = "\n";
        using var csv = new CsvWriter(writer, new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture));
        csv.WriteField("id");
        csv.WriteField("name");
        csv.WriteField("group");
        csv.WriteField("translation");
        csv.WriteField("count");
        csv.NextRecord();
        foreach (var r in rows)
        {
            csv.WriteField(r.Id);
            csv.WriteField(r.Name);
            csv.WriteField(r.Group);
            csv.WriteField(r.Translation);
            csv.WriteField(r.Count);
            csv.NextRecord();
        }
    }

    public static List<CatalogRow> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        using var csv = new CsvReader(reader, new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
        });
        csv.Read();
        csv.ReadHeader();
        var rows = new List<CatalogRow>();
        while (csv.Read())
        {
            rows.Add(new CatalogRow(
                csv.GetField<int>("id"),
                csv.GetField<string>("name")!,
                csv.GetField<string>("group")!,
                csv.GetField<string>("translation") ?? "",
                csv.GetField<long>("count")));
        }
        return rows;
    }
}

public static class Checksums
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void Write(string packRoot)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { PackFiles.Model, PackFiles.Catalog, PackFiles.Manifest, PackFiles.License })
            sb.Append(Sha256(Path.Combine(packRoot, name))).Append("  ").Append(name).Append('\n');
        File.WriteAllText(Path.Combine(packRoot, PackFiles.Checksums), sb.ToString(), new UTF8Encoding(false));
    }

    public static Dictionary<string, string> Read(string packRoot)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(packRoot, PackFiles.Checksums)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new InvalidDataException($"malformed checksum line: {line}");
            map[parts[1]] = parts[0];
        }
        return map;
    }
}
