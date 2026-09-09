using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ImageTagger.Core.Domain;

namespace ImageTagger.App.Controls;

/// <summary>纯视图转换器：领域值到主题色板，绝不抛异常、无业务逻辑。</summary>
public static class ViewConverterKeys
{
    public const string GroupAccent = "GroupAccentConverter";
    public const string Confidence = "ConfidenceBrushConverter";
    public const string AnalysisState = "AnalysisStateBrushConverter";
    public const string ThemeDisplay = "ThemeDisplayConverter";
    public const string AccelerationDisplay = "AccelerationDisplayConverter";
}

/// <summary>分组标识到主题分组色（未知分组回落语义信息色，应用无资源时回落灰色）。</summary>
public sealed class GroupAccentConverter : IValueConverter
{
    public static GroupAccentConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        var resourceKey = key switch
        {
            "rating" => "GroupRatingBrush",
            "character" => "GroupCharacterBrush",
            "general" => "GroupGeneralBrush",
            "artist" => "GroupArtistBrush",
            "copyright" => "GroupCopyrightBrush",
            _ => "SemanticInfoBrush",
        };
        return FindBrush(resourceKey) ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static IBrush? FindBrush(string key)
    {
        try
        {
            var app = Application.Current;
            if (app is not null && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var found))
                return found as IBrush;
        }
        catch (Exception)
        {
            // 设计时/测试宿主无主题资源时回落默认色，绝不让视图崩溃。
        }
        return null;
    }
}

/// <summary>置信度到辅助色：≥90% 成功色，70%–90% 分组信息色，以下次要色。
/// 注意：画刷绑定解析为 null 会直接透明（不继承），因此绝不能返回 null。</summary>
public sealed class ConfidenceBrushConverter : IValueConverter
{
    public static ConfidenceBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double probability = value switch
        {
            float f => f,
            double d => d,
            _ => double.NaN,
        };
        IBrush? brush = null;
        if (!double.IsNaN(probability))
        {
            if (probability >= 0.9)
                brush = GroupAccentConverter.FindBrush("SemanticSuccessBrush");
            else if (probability >= 0.7)
                brush = GroupAccentConverter.FindBrush("SemanticInfoBrush");
        }

        return brush
            ?? GroupAccentConverter.FindBrush("SecondaryTextBrush")
            ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>主题偏好到中文展示。</summary>
public sealed class ThemePreferenceDisplayConverter : IValueConverter
{
    public static ThemePreferenceDisplayConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ThemePreference.FollowSystem => "跟随系统",
            ThemePreference.Light => "浅色",
            ThemePreference.Dark => "深色",
            _ => value?.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>加速策略到中文展示。</summary>
public sealed class AccelerationPreferenceDisplayConverter : IValueConverter
{
    public static AccelerationPreferenceDisplayConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            AccelerationPreference.Auto => "自动",
            AccelerationPreference.PowerSaver => "节能优先",
            AccelerationPreference.CpuOnly => "仅 CPU",
            _ => value?.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>手动排除状态到不透明度：被排除的胶囊半透明但仍可读、仍保留原位。</summary>
public sealed class ExclusionDimConverter : IValueConverter
{
    public static ExclusionDimConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.48 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>识别状态到语义色：成功绿、失败红、运行中蓝、过期黄，其余次要色。
/// 注意：画刷绑定解析为 null 会直接透明（不继承），因此绝不能返回 null。</summary>
public sealed class AnalysisStateBrushConverter : IValueConverter
{
    public static AnalysisStateBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        IBrush? brush = value is AnalysisState state
            ? state switch
            {
                AnalysisState.Succeeded => GroupAccentConverter.FindBrush("SemanticSuccessBrush"),
                AnalysisState.Failed => GroupAccentConverter.FindBrush("SemanticErrorBrush"),
                AnalysisState.Running or AnalysisState.Queued => GroupAccentConverter.FindBrush("SemanticInfoBrush"),
                AnalysisState.Stale => GroupAccentConverter.FindBrush("SemanticWarningBrush"),
                _ => null,
            }
            : null;
        return brush
            ?? GroupAccentConverter.FindBrush("SecondaryTextBrush")
            ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>工作区阶段到状态栏语义色。</summary>
public sealed class WindowPhaseBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ImageTagger.App.ViewModels.WindowPhase.Busy => "SemanticInfoBrush",
            ImageTagger.App.ViewModels.WindowPhase.Error => "SemanticErrorBrush",
            ImageTagger.App.ViewModels.WindowPhase.Succeeded => "SemanticSuccessBrush",
            _ => "SecondaryTextBrush",
        };
        return GroupAccentConverter.FindBrush(key) ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1d : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>图片卡片边框：识别完成为绿色，其余保持默认描边色。</summary>
public sealed class ItemCardBorderConverter : IValueConverter
{
    public static ItemCardBorderConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        GroupAccentConverter.FindBrush(value is AnalysisState.Succeeded ? "SemanticSuccessBrush" : "ThemeBorderBrush")
        ?? Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>滚动容器内边距：纵向内容溢出（滚动条出现）时才在右侧留出悬浮条间隙，
/// 无溢出时用参数指定的基础内边距，不预留空白。绑定 Extent + Viewport。</summary>
public sealed class ScrollGutterConverter : IMultiValueConverter
{
    public static ScrollGutterConverter Instance { get; } = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var basePadding = Thickness.Parse(parameter?.ToString() ?? "0");
        var extent = values.Count > 0 && values[0] is Size extentSize ? extentSize : default;
        var viewport = values.Count > 1 && values[1] is Size viewportSize ? viewportSize : default;
        if (extent.Height > viewport.Height + 0.5)
            return new Thickness(basePadding.Left, basePadding.Top, Math.Max(basePadding.Right, 12), basePadding.Bottom);
        return basePadding;
    }
}
