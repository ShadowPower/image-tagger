using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ShadUI;

namespace ImageTagger.App;

public partial class MainWindow : ShadUI.Window
{
    private const double DefaultRightPaneWidth = 320;
    private const double MinRightPaneWidth = 300;
    private const double MaxRightPaneWidth = 420;

    private MainViewModel? _attachedViewModel;
    private double _rightPaneWidth = DefaultRightPaneWidth;

    public MainWindow() => InitializeComponent();

    /// <summary>Windows 11 圆角：ShadUI 在模板应用时用内部快照覆盖 XAML 设置的值，
    /// 因此在模板就绪后用代码固定；最大化/全屏由主题接管为直角，还原时重新应用。</summary>
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

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsDragOver = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsDragOver = false;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm || !e.DataTransfer.Contains(DataFormat.File)) return;
            var files = e.DataTransfer is IAsyncDataTransfer asyncTransfer
                ? (await asyncTransfer.TryGetFilesAsync())?.Select(x => x.Path.LocalPath)
                    .Where(path => File.Exists(path) || Directory.Exists(path)).ToArray()
                : null;
            if (files is { Length: > 0 }) await vm.ImportPathsAsync(files, recursive: false);
        }
        catch (Exception)
        {
            if (DataContext is MainViewModel vm)
                vm.StatusBarText = "拖拽导入失败，请重试";
        }
        finally
        {
            if (DataContext is MainViewModel vm)
                vm.IsDragOver = false;
            e.Handled = true;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnWindowDragLeave);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AttachViewModel(DataContext as MainViewModel);
        Controls.DwmWindowChrome.EnableSystemShadow(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (IsLoaded)
            AttachViewModel(DataContext as MainViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        AttachViewModel(null);
        base.OnUnloaded(e);
    }

    /// <summary>
    /// 右栏折叠时把列宽置零（同时记住拖拽宽度），展开时恢复。
    /// 不能用绑定：分隔条拖拽会以本地值覆盖绑定，之后开关不再生效。
    /// </summary>
    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_attachedViewModel, viewModel))
            return;
        if (_attachedViewModel is not null)
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _attachedViewModel = viewModel;
        if (_attachedViewModel is not null)
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyRightPaneWidth();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.IsRightPaneExpanded), StringComparison.Ordinal))
            ApplyRightPaneWidth();
    }

    private void ApplyRightPaneWidth()
    {
        if (WorkspaceGrid.ColumnDefinitions.Count < 5 || _attachedViewModel is null)
            return;
        var column = WorkspaceGrid.ColumnDefinitions[4];
        if (_attachedViewModel.IsRightPaneExpanded)
        {
            var restored = _rightPaneWidth is >= MinRightPaneWidth and <= MaxRightPaneWidth
                ? _rightPaneWidth
                : DefaultRightPaneWidth;
            column.Width = new GridLength(restored, GridUnitType.Pixel);
        }
        else
        {
            if (column.ActualWidth > 0)
                _rightPaneWidth = column.ActualWidth;
            column.Width = new GridLength(0, GridUnitType.Pixel);
        }
    }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel viewModel)
            return;

        var editingText = FocusManager?.GetFocusedElement() is TextBox;
        e.Handled = TryHandleSessionKey(viewModel, e.Key, e.KeyModifiers, editingText);
    }

    /// <summary>Applies global shortcuts while preserving normal text editing keys.</summary>
    public static bool TryHandleSessionKey(
        MainViewModel viewModel,
        Key key,
        KeyModifiers modifiers,
        bool isTextEditing)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (modifiers == KeyModifiers.None && key == Key.Escape)
        {
            return Execute(viewModel.CancelCommand);
        }
        if (!isTextEditing && modifiers == KeyModifiers.None)
        {
            if (key == Key.Up)
                return Execute(viewModel.SelectPreviousCommand);
            if (key == Key.Down)
                return Execute(viewModel.SelectNextCommand);
            if (key == Key.Delete)
                return Execute(viewModel.RemoveSelectedCommand);
        }
        else if (modifiers == KeyModifiers.Control && key == Key.O)
        {
            return Execute(viewModel.OpenImagesCommand);
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.O)
        {
            return Execute(viewModel.OpenFolderCommand);
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Alt) && key == Key.C)
        {
            return Execute(viewModel.CopyPromptCommand);
        }
        else if (modifiers == KeyModifiers.Control && key == Key.OemComma)
        {
            return Execute(viewModel.OpenSettingsCommand);
        }
        else if (modifiers == KeyModifiers.Control && key == Key.Enter)
        {
            return Execute(viewModel.RecognizeCurrentCommand);
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.Enter)
        {
            return Execute(viewModel.RecognizeAllCommand);
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.P)
        {
            return Execute(viewModel.ToggleRightPaneCommand);
        }
        return false;
    }

    private void OnRecognizeItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, viewModel => viewModel.RecognizeItemCommand);

    private void OnCopyPathClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, viewModel => viewModel.CopyPathCommand);

    private void OnRevealItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, viewModel => viewModel.RevealItemCommand);

    private void OnRemoveItemClick(object? sender, RoutedEventArgs e) =>
        ExecuteItemCommand(sender, viewModel => viewModel.RemoveItemCommand);

    /// <summary>切换标签的 Prompt 参与状态（D-03；只影响 Prompt，不删除标签结果）。</summary>
    private void OnToggleTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TagItemViewModel tag }
            && DataContext is MainViewModel viewModel
            && viewModel.ToggleTagCommand.CanExecute(tag.Index.ToString()))
        {
            viewModel.ToggleTagCommand.Execute(tag.Index.ToString());
        }
    }

    /// <summary>在两种组内排序之间切换（F-04）。</summary>
    private void OnCycleSortClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GroupRuleRow row }
            && DataContext is MainViewModel viewModel)
        {
            var next = row.SortMode == GroupSortMode.ConfidenceDescending
                ? GroupSortMode.NameAscending
                : GroupSortMode.ConfidenceDescending;
            viewModel.Rules.SetGroupSortMode(row.GroupId, next);
        }
    }

    private void OnRemoveExcludedTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string tag }
            && DataContext is MainViewModel viewModel
            && viewModel.RemoveExcludedTagCommand.CanExecute(tag))
        {
            viewModel.RemoveExcludedTagCommand.Execute(tag);
        }
    }

    private void OnRemoveReplacementClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: System.Collections.Generic.KeyValuePair<string, string> pair }
            && DataContext is MainViewModel viewModel
            && viewModel.RemoveReplacementCommand.CanExecute(pair.Key))
        {
            viewModel.RemoveReplacementCommand.Execute(pair.Key);
        }
    }

    private void OnImageItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ImageListItemViewModel item }
            && DataContext is MainViewModel viewModel)
        {
            viewModel.SelectById(item.Id);
            if (viewModel.OpenItemCommand.CanExecute(item))
                viewModel.OpenItemCommand.Execute(item);
        }
    }

    private void ExecuteItemCommand(
        object? sender,
        Func<MainViewModel, System.Windows.Input.ICommand> commandSelector)
    {
        if (sender is Control { DataContext: ImageListItemViewModel item }
            && DataContext is MainViewModel viewModel)
        {
            var command = commandSelector(viewModel);
            if (command.CanExecute(item))
                command.Execute(item);
        }
    }

    private static bool Execute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
            return false;
        command.Execute(null);
        return true;
    }
}
