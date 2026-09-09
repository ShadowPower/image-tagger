using Avalonia.Input;
using ImageTagger.App.Resources;

namespace ImageTagger.App.Input;

/// <summary>DESIGN 12.3 快捷键标识。</summary>
public enum ShortcutId
{
    OpenImages,
    OpenFolder,
    RecognizeCurrent,
    RecognizeAll,
    Cancel,
    SelectPrevious,
    SelectNext,
    RemoveCurrent,
    ToggleRightPane,
    CopyPrompt,
    OpenSettings,
}

/// <summary>集中注册的快捷键定义：按键 + 修饰键 + 展示文本 + 中文说明。</summary>
public sealed record ShortcutDefinition(
    ShortcutId Id,
    Key Key,
    KeyModifiers Modifiers,
    string DisplayGesture,
    string Description);

/// <summary>图标按钮的 ToolTip 与无障碍名称（DESIGN 17）。</summary>
public sealed record ButtonMetadata(string ToolTip, string AutomationName);

/// <summary>DESIGN 12.3 快捷键总表与按钮元数据的唯一来源。</summary>
public static class AppShortcuts
{
    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        new(ShortcutId.OpenImages, Key.O, KeyModifiers.Control, "Ctrl+O", Strings.Shortcut_OpenImages),
        new(ShortcutId.OpenFolder, Key.O, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+O", Strings.Shortcut_OpenFolder),
        new(ShortcutId.RecognizeCurrent, Key.Enter, KeyModifiers.Control, "Ctrl+Enter", Strings.Shortcut_RecognizeCurrent),
        new(ShortcutId.RecognizeAll, Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+Enter", Strings.Shortcut_RecognizeAll),
        new(ShortcutId.Cancel, Key.Escape, KeyModifiers.None, "Esc", Strings.Shortcut_Cancel),
        new(ShortcutId.SelectPrevious, Key.Up, KeyModifiers.None, "↑", Strings.Shortcut_SelectPrevious),
        new(ShortcutId.SelectNext, Key.Down, KeyModifiers.None, "↓", Strings.Shortcut_SelectNext),
        new(ShortcutId.RemoveCurrent, Key.Delete, KeyModifiers.None, "Delete", Strings.Shortcut_RemoveCurrent),
        new(ShortcutId.ToggleRightPane, Key.P, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+P", Strings.Shortcut_ToggleRightPane),
        new(ShortcutId.CopyPrompt, Key.C, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+C", Strings.Shortcut_CopyPrompt),
        new(ShortcutId.OpenSettings, Key.OemComma, KeyModifiers.Control, "Ctrl+,", Strings.Shortcut_OpenSettings),
    ];

    /// <summary>图标按钮的集中字典：ToolTip + AutomationProperties.Name。</summary>
    public static IReadOnlyDictionary<string, ButtonMetadata> Buttons { get; } =
        new Dictionary<string, ButtonMetadata>(StringComparer.Ordinal)
        {
            ["OpenImages"] = new(Strings.Button_OpenImages_ToolTip, Strings.Button_OpenImages_Name),
            ["OpenFolder"] = new(Strings.Button_OpenFolder_ToolTip, Strings.Button_OpenFolder_Name),
            ["RecognizeCurrent"] = new(Strings.Button_RecognizeCurrent_ToolTip, Strings.Button_RecognizeCurrent_Name),
            ["RecognizeAll"] = new(Strings.Button_RecognizeAll_ToolTip, Strings.Button_RecognizeAll_Name),
            ["Cancel"] = new(Strings.Button_Cancel_ToolTip, Strings.Button_Cancel_Name),
            ["ToggleRightPane"] = new(Strings.Button_ToggleRightPane_ToolTip, Strings.Button_ToggleRightPane_Name),
            ["CopyPrompt"] = new(Strings.Button_CopyPrompt_ToolTip, Strings.Button_CopyPrompt_Name),
            ["OpenSettings"] = new(Strings.Button_OpenSettings_ToolTip, Strings.Button_OpenSettings_Name),
            ["RemoveCurrent"] = new(Strings.Button_RemoveCurrent_ToolTip, Strings.Button_RemoveCurrent_Name),
        };

    /// <summary>按按键查找快捷键；无匹配返回 null。</summary>
    public static ShortcutId? FindByGesture(Key key, KeyModifiers modifiers)
    {
        foreach (var definition in All)
        {
            if (definition.Key == key && definition.Modifiers == modifiers)
                return definition.Id;
        }

        return null;
    }
}
