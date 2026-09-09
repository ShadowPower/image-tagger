using System.Text.Json;
using System.Text.Json.Serialization;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Settings;

/// <summary>
/// 单套 Prompt 规则持久化（prompt-settings.json）。
/// 300ms 防抖后台写 + flush 立即写，写临时文件 + flush + 原子替换，
/// 损坏保留备份并恢复默认。JSON 用 camelCase，schemaVersion=1 信封。
/// “恢复默认”由调用方 Save(Default)+flush 实现，本类不隐含重置逻辑。
/// </summary>
public sealed class PromptSettingsStore : IPromptSettingsStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "prompt-settings.json";
    private const int DebounceMilliseconds = 300;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private PromptSettings? _pending;
    private Timer? _debounceTimer;
    private bool _disposed;

    public PromptSettingsStore(IAppResourceLocator resourceLocator)
        : this(resourceLocator?.SettingsRoot ?? throw new ArgumentNullException(nameof(resourceLocator)))
    {
    }

    /// <summary>以显式应用数据目录为根创建存储（测试使用临时目录）。</summary>
    public PromptSettingsStore(string settingsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);
        _settingsPath = Path.Combine(settingsRoot, FileName);
    }

    /// <summary>文件缺失、不可读或损坏时返回默认（并保留损坏备份）。有未落盘的防抖写入时优先返回最新值。</summary>
    public PromptSettings Load()
    {
        lock (_gate)
        {
            if (_pending is not null)
                return _pending;

            if (!File.Exists(_settingsPath))
                return PromptSettingsFactory.CreateDefault();

            try
            {
                var json = File.ReadAllText(_settingsPath);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                    MaxDepth = 32,
                });

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new JsonException("提示词设置根必须为对象。");

                if (!root.TryGetProperty("schemaVersion", out var versionElement)
                    || !versionElement.TryGetInt32(out var version)
                    || version != CurrentSchemaVersion)
                    throw new JsonException("不支持的提示词设置版本。");

                var envelope = JsonSerializer.Deserialize<PromptSettingsEnvelope>(json, JsonOptions)
                    ?? throw new JsonException("提示词设置内容为空。");
                var settings = envelope.Settings ?? throw new JsonException("提示词设置对象缺失。");
                return Validate(settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                PreserveInvalidFile();
                return PromptSettingsFactory.CreateDefault();
            }
        }
    }

    /// <summary>排队防抖原子写；flush 为 true 时立即写入。</summary>
    public void Save(PromptSettings settings, bool flush = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Validate(settings);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = settings;
            if (flush)
            {
                WriteAtomically(settings);
                _pending = null;
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _debounceTimer ??= new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
                _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
        }
    }

    /// <summary>立即落盘当前排队值（无排队时为空操作），供测试与退出路径使用。</summary>
    public void Flush()
    {
        PromptSettings? pending;
        lock (_gate)
        {
            pending = _pending;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (pending is not null)
        {
            var validated = Validate(pending);
            lock (_gate)
            {
                WriteAtomically(validated);
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        PromptSettings? pending;
        lock (_gate)
        {
            pending = _pending;
        }
        if (pending is null)
            return;
        try
        {
            var validated = Validate(pending);
            lock (_gate)
            {
                // 若期间已有更新的排队值，仅写入最新值。
                var latest = _pending ?? validated;
                WriteAtomically(Validate(latest));
                if (ReferenceEquals(_pending, pending) || _pending is not null)
                {
                    // 后到的排队值已在 latest 中一并落盘，直接清空。
                    _pending = null;
                }
            }
        }
        catch (IOException)
        {
            // 后台写失败保留排队值，下次 Flush 重试；不抛到线程池。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteAtomically(PromptSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var envelope = new PromptSettingsEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                Settings = settings,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static PromptSettings Validate(PromptSettings settings)
    {
        if (settings.Output.TagSeparator is null || settings.Output.TagSeparator.Length == 0)
            throw new JsonException("标签分隔符不允许为空。");
        if (settings.Output.GroupSeparator is null || settings.Output.GroupSeparator.Length == 0)
            throw new JsonException("分组分隔符不允许为空。");
        if (settings.Output.MaxTags is < 1 or > 500)
            throw new JsonException("最大标签数范围为 1..500。");
        if (!Enum.IsDefined(settings.Output.QualityPreset))
            throw new JsonException("未知质量标签预设。");
        foreach (var pair in settings.Thresholds.GroupThresholds)
        {
            if (string.IsNullOrEmpty(pair.Key))
                throw new JsonException("分组阈值的组标识不允许为空。");
            if (!double.IsFinite(pair.Value) || pair.Value is < 0 or > 1)
                throw new JsonException($"分组阈值 '{pair.Key}' 范围为 0..1。");
        }
        if (!double.IsFinite(settings.Transforms.MinWeight) || settings.Transforms.MinWeight is < 0.5 or > 5)
            throw new JsonException("权重下限范围为 0.5..5。");
        if (!double.IsFinite(settings.Transforms.MaxWeight) || settings.Transforms.MaxWeight is < 0.5 or > 5)
            throw new JsonException("权重上限范围为 0.5..5。");
        if (settings.Transforms.MinWeight > settings.Transforms.MaxWeight)
            throw new JsonException("权重下限不得大于上限。");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in settings.GroupRules)
        {
            if (rule is null || string.IsNullOrEmpty(rule.GroupId))
                throw new JsonException("分组规则的组标识不允许为空。");
            if (!groupIds.Add(rule.GroupId))
                throw new JsonException($"分组规则 '{rule.GroupId}' 重复。");
            if (!Enum.IsDefined(rule.SortMode))
                throw new JsonException("未知组内排序。");
        }
        if (settings.Transforms.ExcludedTags.Any(tag => tag is null))
            throw new JsonException("排除列表不允许含空值。");
        if (settings.Transforms.Replacements.Any(pair => pair.Key is null || pair.Value is null))
            throw new JsonException("替换规则不允许含空值。");
        return settings;
    }

    private void PreserveInvalidFile()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return;
            var directory = Path.GetDirectoryName(_settingsPath)!;
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var backupPath = Path.Combine(
                directory,
                $"prompt-settings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
            File.Move(_settingsPath, backupPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record PromptSettingsEnvelope
    {
        public int SchemaVersion { get; init; }

        public PromptSettings? Settings { get; init; }
    }
}
