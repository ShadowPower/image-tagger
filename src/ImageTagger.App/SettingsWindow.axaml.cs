using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace ImageTagger.App;

public partial class SettingsWindow : ShadUI.Window
{
    public SettingsWindow() => InitializeComponent();

    /// <summary>Windows 11 圆角：与主窗口同理，模板就绪后用代码固定（XAML 设置会被主题内部快照覆盖）。</summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        RootCornerRadius = new CornerRadius(8);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == Avalonia.Controls.WindowState.Normal)
            RootCornerRadius = new CornerRadius(8);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Controls.DwmWindowChrome.EnableSystemShadow(this);
    }
}
