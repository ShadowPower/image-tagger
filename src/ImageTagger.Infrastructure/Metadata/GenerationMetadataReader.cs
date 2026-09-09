using System.Collections;
using System.Text;
using SixLabors.ImageSharp;
using MetadataDirectory = MetadataExtractor.Directory;
using MetadataTag = MetadataExtractor.Tag;

namespace ImageTagger.Infrastructure.Metadata;

/// <summary>
/// Reads candidate AI-generation text metadata from image files.
///
/// Container decoding is fully delegated to libraries: MetadataExtractor reads
/// PNG text chunks, EXIF and XMP containers, while the ImageSharp metadata API
/// supplements PNG text, EXIF and XMP profiles. This type never parses image
/// container bytes itself; it only normalizes the resulting key/value pairs.
///
/// Returns a read-only dictionary whose keys keep the original
/// "directory/tag" shape. Oversized values are truncated and reported through
/// synthetic "Warning/N" entries so the fixed dictionary return type can still
/// carry truncation and corruption warnings to <see cref="GenerationInfoParser"/>.
/// </summary>
public sealed class GenerationMetadataReader
{
    /// <summary>Maximum displayed length of a single metadata field (2 MiB).</summary>
    public const int MaxFieldLength = 2 * 1024 * 1024;

    /// <summary>Maximum summed length of all raw values (8 MiB).</summary>
    public const int MaxTotalLength = 8 * 1024 * 1024;

    /// <summary>Maximum JSON depth accepted by downstream parsers.</summary>
    public const int MaxJsonDepth = 64;

    private const string WarningKeyPrefix = "Warning/";

    /// <summary>
    /// Reads raw candidate metadata without decoding pixel buffers.
    /// Corrupt directories or fields never discard the remaining metadata.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadRawAsync(
        string canonicalPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        cancellationToken.ThrowIfCancellationRequested();

        var collected = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        await Task.Run(() => ReadWithMetadataExtractor(canonicalPath, collected, warnings), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await SupplementWithImageSharpAsync(canonicalPath, collected, warnings, cancellationToken)
            .ConfigureAwait(false);

        ApplyTotalBudget(collected, warnings);
        for (var i = 0; i < warnings.Count; i++)
        {
            collected[$"{WarningKeyPrefix}{i}"] = warnings[i];
        }

        return collected;
    }

    private static void ReadWithMetadataExtractor(
        string canonicalPath,
        Dictionary<string, string> collected,
        List<string> warnings)
    {
        IReadOnlyList<MetadataDirectory> directories;
        try
        {
            directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(canonicalPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"元数据容器读取失败，已尝试补充读取：{TrimWarning(exception.Message)}");
            return;
        }

        foreach (var directory in directories)
        {
            try
            {
                ReadOneDirectory(directory, collected, warnings);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"目录“{directory.Name}”读取失败，已保留其余元数据。");
            }
        }
    }

    private static void ReadOneDirectory(
        MetadataDirectory directory,
        Dictionary<string, string> collected,
        List<string> warnings)
    {
        if (directory.HasError)
        {
            foreach (var error in directory.Errors)
            {
                warnings.Add($"目录“{directory.Name}”部分损坏：{TrimWarning(error)}");
            }
        }

        foreach (var tag in directory.Tags)
        {
            try
            {
                if (TryExpandComplexValue(directory, tag, collected, warnings))
                {
                    continue;
                }

                var text = tag.Description;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                AddWithFieldLimit(collected, warnings, $"{directory.Name}/{tag.Name}", text);
            }
            catch (Exception)
            {
                warnings.Add($"字段“{directory.Name}/{tag.Name}”损坏，已保留其余元数据。");
            }
        }
    }

    /// <summary>
    /// Expands multi-valued textual chunks (for example several PNG tEXt keywords
    /// collapsed under one tag) into separate entries. Returns false when the tag
    /// is a plain scalar that the caller should read via <c>Tag.Description</c>.
    /// Binary payloads fall back to the library description (such as "42 bytes")
    /// instead of being decoded as text here.
    /// </summary>
    private static bool TryExpandComplexValue(
        MetadataDirectory directory,
        MetadataTag tag,
        Dictionary<string, string> collected,
        List<string> warnings)
    {
        object? raw;
        try
        {
            raw = directory.GetObject(tag.Type);
        }
        catch (Exception)
        {
            return false;
        }

        if (raw is null or string or byte[])
        {
            return false;
        }

        if (raw is IDictionary dictionary)
        {
            var expanded = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                var keyword = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
                var value = entry.Value as string ?? Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(keyword) || value is null)
                {
                    continue;
                }

                AddWithFieldLimit(collected, warnings, $"{directory.Name}/{keyword.Trim()}", value);
                expanded++;
            }

            return expanded > 0;
        }

        if (raw is not IEnumerable enumerable)
        {
            return false;
        }

        var added = 0;
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            if (TrySplitTextualItem(item, out var keyword, out var value)
                && !string.IsNullOrEmpty(keyword) && value is not null)
            {
                AddWithFieldLimit(collected, warnings, $"{directory.Name}/{keyword.Trim()}", value);
                added++;
            }
        }

        return added > 0;
    }

    private static bool TrySplitTextualItem(object item, out string keyword, out string? value)
    {
        keyword = string.Empty;
        value = null;
        var type = item.GetType();
        var keywordProperty = FindProperty(type, "Keyword", "Key", "Name");
        var valueProperty = FindProperty(type, "Value", "Text", "Description");
        if (keywordProperty is null || valueProperty is null)
        {
            return false;
        }

        try
        {
            keyword = Convert.ToString(
                keywordProperty.GetValue(item), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var rawValue = valueProperty.GetValue(item);
            value = rawValue as string
                ?? Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static System.Reflection.PropertyInfo? FindProperty(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase);
            if (property is not null && property.CanRead)
            {
                return property;
            }
        }

        return null;
    }

    private static async Task SupplementWithImageSharpAsync(
        string canonicalPath,
        Dictionary<string, string> collected,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        ImageInfo info;
        try
        {
            info = await Image.IdentifyAsync(canonicalPath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("无法识别图片信息。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Add($"补充元数据读取失败，已保留已有结果：{TrimWarning(exception.Message)}");
            return;
        }

        try
        {
            foreach (var text in info.Metadata.GetPngMetadata().TextData)
            {
                try
                {
                    if (string.IsNullOrEmpty(text.Keyword) || text.Value is null)
                    {
                        continue;
                    }

                    AddWithFieldLimit(collected, warnings, $"ImageSharp/Png:{text.Keyword}", text.Value);
                }
                catch (Exception)
                {
                    warnings.Add("PNG文本块中存在损坏字段，已保留其余元数据。");
                }
            }
        }
        catch (Exception)
        {
            warnings.Add("PNG补充元数据读取失败，已保留其余元数据。");
        }

        try
        {
            var exif = info.Metadata.ExifProfile;
            if (exif is not null)
            {
                foreach (var value in exif.Values)
                {
                    try
                    {
                        var text = ExifValueToString(value.GetValue());
                        if (string.IsNullOrEmpty(text))
                        {
                            continue;
                        }

                        AddWithFieldLimit(collected, warnings, $"ImageSharp/Exif:{value.Tag}", text);
                    }
                    catch (Exception)
                    {
                        warnings.Add("EXIF中存在损坏字段，已保留其余元数据。");
                    }
                }
            }
        }
        catch (Exception)
        {
            warnings.Add("EXIF补充元数据读取失败，已保留其余元数据。");
        }

        try
        {
            var xmp = info.Metadata.XmpProfile;
            if (xmp is not null)
            {
                var data = xmp.ToByteArray();
                if (data is { Length: > 0 })
                {
                    var text = Encoding.UTF8.GetString(data);
                    if (!string.IsNullOrEmpty(text))
                    {
                        AddWithFieldLimit(collected, warnings, "ImageSharp/Xmp", text);
                    }
                }
            }
        }
        catch (Exception)
        {
            warnings.Add("XMP补充元数据读取失败，已保留其余元数据。");
        }
    }

    internal static string? ExifValueToString(object? value) => value switch
    {
        null => null,
        string text => text,
        byte[] bytes => DecodeExifBytes(bytes),
        Array array => string.Join(
            ", ",
            array.Cast<object?>().Select(item => item is byte[] nested ? DecodeExifBytes(nested) : Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? DecodeExifBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        // EXIF UserComment carries an 8-byte charset preamble such as ASCII or
        // UNICODE padding; strip it so the payload stays readable as plain text.
        var offset = 0;
        if (bytes.Length > 8)
        {
            var preamble = Encoding.ASCII.GetString(bytes, 0, 8);
            if (preamble.StartsWith("ASCII", StringComparison.Ordinal)
                || preamble.StartsWith("UNICODE", StringComparison.Ordinal)
                || preamble.StartsWith("JIS", StringComparison.Ordinal))
            {
                offset = 8;
            }
        }

        var text = offset == 0
            ? Encoding.UTF8.GetString(bytes)
            : Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        text = text.TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    internal static void AddWithFieldLimit(
        Dictionary<string, string> collected, List<string> warnings, string key, string value)
    {
        var stored = value;
        if (stored.Length > MaxFieldLength)
        {
            stored = stored.Substring(0, MaxFieldLength);
            warnings.Add($"字段“{key}”超过2MiB，已截断展示。");
        }

        collected[ResolveKeyCollision(collected, key)] = stored;
    }

    private static string ResolveKeyCollision(Dictionary<string, string> collected, string key)
    {
        if (!collected.ContainsKey(key))
        {
            return key;
        }

        var suffix = 2;
        while (collected.ContainsKey($"{key} #{suffix}"))
        {
            suffix++;
        }

        return $"{key} #{suffix}";
    }

    private static void ApplyTotalBudget(Dictionary<string, string> collected, List<string> warnings)
    {
        long total = collected.Values.Sum(value => (long)value.Length);
        if (total <= MaxTotalLength)
        {
            return;
        }

        var orderedKeys = collected.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
        long kept = 0;
        var keptCount = 0;
        foreach (var key in orderedKeys)
        {
            var length = collected[key].Length;
            if (kept + length > MaxTotalLength)
            {
                collected.Remove(key);
                continue;
            }

            kept += length;
            keptCount++;
        }

        warnings.Add($"原始文本总和超过8MiB，已截断保留前{keptCount}个字段。");
    }

    private static string TrimWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知错误";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200);
    }
}
