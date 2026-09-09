using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageTagger.App.Resources;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;

namespace ImageTagger.App.ViewModels;

/// <summary>Settings for one external model folder plus UI/runtime preferences.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IModelPackService _models;
    private readonly IPlatformService _platform;
    private readonly IAppResourceLocator _resources;
    private AppSettings _settings;
    private bool _loading;

    public SettingsViewModel(ISettingsStore store, IModelPackService models,
        IPlatformService platform, IAppResourceLocator resources)
    {
        _store = store; _models = models; _platform = platform; _resources = resources;
        _settings = store.Load();
        _loading = true;
        ModelPackPath = _settings.ModelPackPath;
        Theme = _settings.Theme;
        RecursiveFolderScan = _settings.RecursiveFolderScan;
        RestoreWindowLayout = _settings.RestoreWindowLayout;
        ShowChineseTranslation = _settings.ShowChineseTranslation;
        Acceleration = _settings.Acceleration;
        _loading = false;
        RefreshModelName();
        StatusMessage = Strings.Settings_ImmediateEffective;
    }

    public Action? ClearThumbnailCache { get; set; }
    public Action? ClearSessionPredictions { get; set; }
    public Action? ClearTuningCache { get; set; }
    public Action<ThemePreference>? ApplyTheme { get; set; }
    public Action<bool>? ApplyChineseTranslation { get; set; }
    public Action<bool>? ApplyRecursiveFolderScan { get; set; }
    public Action<string>? ApplyModelPackPath { get; set; }
    public Func<CancellationToken, Task<bool>>? ConfirmClearLocalData { get; set; }

    public IReadOnlyList<ThemePreference> AvailableThemes { get; } =
        [ThemePreference.FollowSystem, ThemePreference.Light, ThemePreference.Dark];
    public IReadOnlyList<AccelerationPreference> AvailableAccelerations { get; } =
        [AccelerationPreference.Auto, AccelerationPreference.PowerSaver, AccelerationPreference.CpuOnly];
    public string AccelerationNote => Strings.Settings_ImmediateEffective;

    [ObservableProperty] private string _modelPackPath = string.Empty;
    [ObservableProperty] private string _modelPackDisplayName = "未配置模型";
    [ObservableProperty] private ThemePreference _theme;
    [ObservableProperty] private bool _recursiveFolderScan;
    [ObservableProperty] private bool _restoreWindowLayout = true;
    [ObservableProperty] private bool _showChineseTranslation = true;
    [ObservableProperty] private AccelerationPreference _acceleration;
    [ObservableProperty] private bool _isRestartRequired;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task SelectModelPackFolderAsync(CancellationToken cancellationToken)
    {
        var path = await _platform.PickFolderAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var descriptor = _models.ReadDescriptorFromPath(path);
            ModelPackPath = Path.GetFullPath(path);
            ModelPackDisplayName = descriptor.DisplayName;
            ErrorMessage = null;
            StatusMessage = "模型目录已配置";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"模型目录无效：{exception.Message}";
        }
    }

    [RelayCommand]
    private void ClearModelPackFolder()
    {
        ModelPackPath = string.Empty;
        ModelPackDisplayName = "未配置模型";
        StatusMessage = "模型配置已清除";
    }

    [RelayCommand]
    public void Recalibrate()
    {
        ClearTuningCache?.Invoke();
        IsRestartRequired = false;
        StatusMessage = Strings.Settings_Recalibrated;
    }

    [RelayCommand]
    public void ClearMemoryCache()
    {
        ClearThumbnailCache?.Invoke();
        ClearSessionPredictions?.Invoke();
        StatusMessage = Strings.Settings_CacheCleared;
    }

    [RelayCommand]
    private async Task ClearLocalDataAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || ConfirmClearLocalData is not null &&
            !await ConfirmClearLocalData(cancellationToken).ConfigureAwait(true)) return;
        IsBusy = true;
        try
        {
            ClearMemoryCache();
            foreach (var directory in new[] { _resources.CacheRoot, _resources.LogsRoot })
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _settings = new AppSettings();
            _store.Save(_settings);
            _loading = true;
            ModelPackPath = string.Empty;
            ModelPackDisplayName = "未配置模型";
            Theme = _settings.Theme;
            RecursiveFolderScan = _settings.RecursiveFolderScan;
            RestoreWindowLayout = _settings.RestoreWindowLayout;
            ShowChineseTranslation = _settings.ShowChineseTranslation;
            Acceleration = _settings.Acceleration;
            _loading = false;
            ApplyModelPackPath?.Invoke(string.Empty);
            StatusMessage = "本地数据已清理";
        }
        finally { IsBusy = false; }
    }

    partial void OnModelPackPathChanged(string value) { Save(); if (!_loading) ApplyModelPackPath?.Invoke(value); }
    partial void OnThemeChanged(ThemePreference value) { Save(); if (!_loading) ApplyTheme?.Invoke(value); }
    partial void OnRecursiveFolderScanChanged(bool value) { Save(); if (!_loading) ApplyRecursiveFolderScan?.Invoke(value); }
    partial void OnRestoreWindowLayoutChanged(bool value) => Save();
    partial void OnShowChineseTranslationChanged(bool value) { Save(); if (!_loading) ApplyChineseTranslation?.Invoke(value); }
    partial void OnAccelerationChanged(AccelerationPreference value) { IsRestartRequired = false; Save(); }

    private void Save()
    {
        if (_loading) return;
        _settings = _settings with
        {
            ModelPackPath = ModelPackPath,
            Theme = Theme,
            RecursiveFolderScan = RecursiveFolderScan,
            RestoreWindowLayout = RestoreWindowLayout,
            ShowChineseTranslation = ShowChineseTranslation,
            Acceleration = Acceleration,
        };
        _store.Save(_settings);
    }

    private void RefreshModelName()
    {
        if (string.IsNullOrWhiteSpace(ModelPackPath)) return;
        try { ModelPackDisplayName = _models.ReadDescriptorFromPath(ModelPackPath).DisplayName; }
        catch { ModelPackDisplayName = "模型目录不可用"; }
    }
}
