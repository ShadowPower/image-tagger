using System.Text.Json;
using System.Text.Json.Serialization;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Settings;

/// <summary>
/// Versioned application-settings persistence. Writes stay in the destination
/// directory so the final overwrite is a same-volume atomic rename.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly ImageTaggerJsonContext JsonContext = new(JsonOptions);
    private readonly object _gate = new();
    private readonly string _settingsPath;

    public SettingsStore(IAppResourceLocator resourceLocator)
        : this(resourceLocator?.SettingsRoot ?? throw new ArgumentNullException(nameof(resourceLocator)))
    {
    }

    /// <summary>Creates a store rooted in an explicitly supplied app-data directory.</summary>
    public SettingsStore(string settingsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);
        _settingsPath = Path.Combine(settingsRoot, FileName);
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

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
                    throw new JsonException("The settings root must be an object.");

                // Pre-versioned development builds wrote AppSettings directly. Treat
                // a missing version as v0 and migrate it in memory.
                if (!root.TryGetProperty("schemaVersion", out var versionElement))
                    return Validate(DeserializeLegacy(json));

                if (!versionElement.TryGetInt32(out var version) || version != CurrentSchemaVersion)
                    throw new JsonException($"Unsupported settings schema version '{versionElement}'.");

                var envelope = JsonSerializer.Deserialize(json, JsonContext.SettingsEnvelope)
                    ?? throw new JsonException("Settings content is empty.");
                return Validate(envelope.Settings ?? throw new JsonException("The settings object is missing."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                PreserveInvalidFile();
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Validate(settings);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                var envelope = new SettingsEnvelope
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Settings = settings,
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonContext.SettingsEnvelope);

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
                    // A failed cleanup must not hide the original save error.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static AppSettings DeserializeLegacy(string json) =>
        JsonSerializer.Deserialize(json, JsonContext.AppSettings)
        ?? throw new JsonException("Legacy settings content is empty.");

    private static AppSettings Validate(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
            throw new JsonException("Unknown theme preference.");
        if (!Enum.IsDefined(settings.Acceleration))
            throw new JsonException("Unknown acceleration preference.");
        if (!string.Equals(settings.UiLanguage, "zh-CN", StringComparison.Ordinal))
            throw new JsonException("Unsupported UI language.");
        if (!string.IsNullOrWhiteSpace(settings.ModelPackPath))
        {
            try { _ = Path.GetFullPath(settings.ModelPackPath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new JsonException("Model Pack 路径无效。", exception); }
        }

        var layout = settings.WindowLayout
            ?? throw new JsonException("Window layout is missing.");
        ValidateFiniteRange(layout.WindowWidth, 800, 16_384, nameof(layout.WindowWidth));
        ValidateFiniteRange(layout.WindowHeight, 600, 16_384, nameof(layout.WindowHeight));
        ValidateFiniteRange(layout.LeftPaneWidth, 240, 420, nameof(layout.LeftPaneWidth));
        ValidateFiniteRange(layout.RightPaneWidth, 320, 480, nameof(layout.RightPaneWidth));
        return settings;
    }

    private static void ValidateFiniteRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new JsonException($"{name} is outside the supported range.");
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
                $"settings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
            File.Move(_settingsPath, backupPath);
        }
        catch (IOException)
        {
            // Best effort: when the file is locked, leave the original untouched.
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
        options.Converters.Add(new JsonStringEnumConverter<ThemePreference>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<AccelerationPreference>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    internal sealed record SettingsEnvelope
    {
        public int SchemaVersion { get; init; }

        public AppSettings? Settings { get; init; }
    }
}
