using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImageTagger.App.Input;

/// <summary>
/// 全局快捷键分发（DESIGN 12.3 / TASKS G-06）。
/// 文本编辑焦点下不抢占常规编辑行为：除 Esc 与复制外，其余全局快捷键跳过。
/// </summary>
public sealed class ShortcutManager
{
    private readonly IReadOnlyDictionary<ShortcutId, Action> _handlers;

    public ShortcutManager(IReadOnlyDictionary<ShortcutId, Action> handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <summary>
    /// 文本编辑焦点判断：TextBox 系（含 MaskedTextBox、自动完成、ComboBox 可编辑态）视为编辑中。
    /// </summary>
    public static bool IsTextEditingFocused(IInputElement? focused)
    {
        if (focused is null)
            return false;
        if (focused is TextBox)
            return true;
        if (focused is ComboBox comboBox && comboBox.IsEditable)
            return true;
        var typeName = focused.GetType().Name;
        return typeName.Contains("TextBox", StringComparison.Ordinal)
            || typeName.Contains("TextPresenter", StringComparison.Ordinal)
            || typeName.Contains("AutoComplete", StringComparison.Ordinal);
    }

    /// <summary>文本编辑焦点下是否仍处理该快捷键（仅 Esc 与复制放行）。</summary>
    public static bool ShouldHandle(ShortcutId id, IInputElement? focused)
    {
        if (!IsTextEditingFocused(focused))
            return true;
        return id is ShortcutId.Cancel or ShortcutId.CopyPrompt;
    }

    /// <summary>按标识执行；文本编辑冲突或无处理器时返回 false。</summary>
    public bool TryExecute(ShortcutId id, IInputElement? focused)
    {
        if (!ShouldHandle(id, focused))
            return false;
        if (!_handlers.TryGetValue(id, out var handler))
            return false;
        handler();
        return true;
    }

    /// <summary>按键分发：先查表再按焦点规则执行。</summary>
    public bool TryHandleKey(Key key, KeyModifiers modifiers, IInputElement? focused)
    {
        var id = AppShortcuts.FindByGesture(key, modifiers);
        if (id is null)
            return false;
        return TryExecute(id.Value, focused);
    }

    /// <summary>
    /// 挂载到 Avalonia 顶层 KeyDown 的桥接示例（Code-behind 调用）。
    /// 业务逻辑仍在 ViewModel 命令中，本方法只做路由。
    /// </summary>
    public void AttachToKeyDown(IInputElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is Interactive interactive)
            interactive.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        var focused = args.Source as IInputElement;
        if (TryHandleKey(args.Key, args.KeyModifiers, focused))
            args.Handled = true;
    }
}
