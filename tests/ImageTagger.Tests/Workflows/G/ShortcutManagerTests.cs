using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ImageTagger.App.Input;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

[Trait("Category", TestCategories.Unit)]
public sealed class ShortcutManagerTests
{
    [Fact]
    public void All_design_shortcuts_are_registered_without_duplicates()
    {
        var all = AppShortcuts.All;
        Assert.Equal(11, all.Count);
        Assert.Equal(11, all.Select(item => item.Id).Distinct().Count());
        Assert.Equal(11, all.Select(item => (item.Key, item.Modifiers)).Distinct().Count());

        Assert.NotNull(AppShortcuts.FindByGesture(Key.O, KeyModifiers.Control));
        Assert.Equal(ShortcutId.OpenImages, AppShortcuts.FindByGesture(Key.O, KeyModifiers.Control));
        Assert.Equal(ShortcutId.OpenFolder, AppShortcuts.FindByGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ShortcutId.RecognizeCurrent, AppShortcuts.FindByGesture(Key.Enter, KeyModifiers.Control));
        Assert.Equal(ShortcutId.RecognizeAll, AppShortcuts.FindByGesture(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ShortcutId.Cancel, AppShortcuts.FindByGesture(Key.Escape, KeyModifiers.None));
        Assert.Equal(ShortcutId.SelectPrevious, AppShortcuts.FindByGesture(Key.Up, KeyModifiers.None));
        Assert.Equal(ShortcutId.SelectNext, AppShortcuts.FindByGesture(Key.Down, KeyModifiers.None));
        Assert.Equal(ShortcutId.RemoveCurrent, AppShortcuts.FindByGesture(Key.Delete, KeyModifiers.None));
        Assert.Equal(ShortcutId.ToggleRightPane, AppShortcuts.FindByGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(ShortcutId.CopyPrompt, AppShortcuts.FindByGesture(Key.C, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Equal(ShortcutId.OpenSettings, AppShortcuts.FindByGesture(Key.OemComma, KeyModifiers.Control));
    }

    [Fact]
    public void Button_metadata_covers_toolbar_with_accessible_names()
    {
        foreach (var key in new[] { "OpenImages", "OpenFolder", "RecognizeCurrent", "RecognizeAll", "Cancel", "ToggleRightPane", "CopyPrompt", "OpenSettings" })
        {
            Assert.True(AppShortcuts.Buttons.ContainsKey(key), $"missing button {key}");
            var metadata = AppShortcuts.Buttons[key];
            Assert.False(string.IsNullOrWhiteSpace(metadata.ToolTip));
            Assert.False(string.IsNullOrWhiteSpace(metadata.AutomationName));
        }
    }

    [AvaloniaFact]
    public void Text_editing_focus_does_not_preempt_global_shortcuts_except_cancel_and_copy()
    {
        var textBox = new TextBox();
        var button = new Button();

        Assert.True(ShortcutManager.IsTextEditingFocused(textBox));
        Assert.False(ShortcutManager.IsTextEditingFocused(button));
        Assert.False(ShortcutManager.IsTextEditingFocused(null));

        Assert.False(ShortcutManager.ShouldHandle(ShortcutId.OpenImages, textBox));
        Assert.False(ShortcutManager.ShouldHandle(ShortcutId.SelectNext, textBox));
        Assert.False(ShortcutManager.ShouldHandle(ShortcutId.RemoveCurrent, textBox));
        Assert.True(ShortcutManager.ShouldHandle(ShortcutId.Cancel, textBox));
        Assert.True(ShortcutManager.ShouldHandle(ShortcutId.CopyPrompt, textBox));
        Assert.True(ShortcutManager.ShouldHandle(ShortcutId.OpenImages, null));
        Assert.True(ShortcutManager.ShouldHandle(ShortcutId.SelectNext, button));
    }

    [AvaloniaFact]
    public void Manager_routes_keys_and_respects_editing_focus()
    {
        var textBox = new TextBox();
        var counts = new Dictionary<ShortcutId, int>();
        var handlers = AppShortcuts.All.ToDictionary(
            item => item.Id,
            item => (Action)(() => counts[item.Id] = counts.TryGetValue(item.Id, out var current) ? current + 1 : 1));
        var manager = new ShortcutManager(handlers);

        Assert.False(manager.TryHandleKey(Key.O, KeyModifiers.Control, textBox));
        Assert.False(counts.ContainsKey(ShortcutId.OpenImages));

        Assert.True(manager.TryHandleKey(Key.Escape, KeyModifiers.None, textBox));
        Assert.Equal(1, counts[ShortcutId.Cancel]);

        Assert.True(manager.TryHandleKey(Key.O, KeyModifiers.Control, null));
        Assert.Equal(1, counts[ShortcutId.OpenImages]);

        Assert.False(manager.TryHandleKey(Key.A, KeyModifiers.Control, null));
    }
}
