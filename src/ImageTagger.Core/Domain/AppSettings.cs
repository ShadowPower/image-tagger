namespace ImageTagger.Core.Domain;

/// <summary>Acceleration strategy from the settings page (design 11.3).</summary>
public enum AccelerationPreference
{
    Auto,
    PowerSaver,
    CpuOnly,
}

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark,
}

/// <summary>Persisted main-window layout values (design 4.2).</summary>
public sealed record WindowLayoutSettings
{
    public double WindowWidth { get; init; } = 1440;

    public double WindowHeight { get; init; } = 900;

    public bool IsMaximized { get; init; }

    public bool IsLeftPaneExpanded { get; init; } = true;

    public double LeftPaneWidth { get; init; } = 286;

    public bool IsRightPaneExpanded { get; init; } = true;

    public double RightPaneWidth { get; init; } = 360;
}

/// <summary>Persisted application settings (settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>Configured external Model Pack directory; empty means not configured.</summary>
    public string ModelPackPath { get; init; } = "";

    public ThemePreference Theme { get; init; } = ThemePreference.FollowSystem;

    /// <summary>UI language; first version ships only zh-CN.</summary>
    public string UiLanguage { get; init; } = "zh-CN";

    public bool RecursiveFolderScan { get; init; }

    public bool RestoreWindowLayout { get; init; } = true;

    public bool ShowChineseTranslation { get; init; } = true;

    public AccelerationPreference Acceleration { get; init; } = AccelerationPreference.Auto;

    /// <summary>
    /// Last usable window layout. Consumers honor it only when
    /// <see cref="RestoreWindowLayout"/> is enabled.
    /// </summary>
    public WindowLayoutSettings WindowLayout { get; init; } = new();
}
