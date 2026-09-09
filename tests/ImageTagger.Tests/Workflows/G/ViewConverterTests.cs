using System.Globalization;
using ImageTagger.App.Controls;
using ImageTagger.Core.Domain;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

/// <summary>
/// 视图画刷转换器永不返回 null：画刷绑定解析为 null 会直接透明（不继承），
/// 曾导致“未识别”状态行与低置信度百分比隐形。无 UI 线程依赖。
/// </summary>
public sealed class ViewConverterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Confidence_returns_visible_brush_for_every_band_and_garbage()
    {
        var converter = new ConfidenceBrushConverter();
        object?[] inputs = [0.99f, 0.9f, 0.899f, 0.7f, 0.699f, 0.0f, float.NaN, "garbage", null];

        foreach (var input in inputs)
        {
            var brush = converter.Convert(input, typeof(object), null, CultureInfo.InvariantCulture);
            Assert.NotNull(brush);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Analysis_state_returns_visible_brush_for_every_state_and_garbage()
    {
        var converter = new AnalysisStateBrushConverter();
        foreach (var state in Enum.GetValues<AnalysisState>())
        {
            var brush = converter.Convert(state, typeof(object), null, CultureInfo.InvariantCulture);
            Assert.NotNull(brush);
        }

        Assert.NotNull(converter.Convert("garbage", typeof(object), null, CultureInfo.InvariantCulture));
        Assert.NotNull(converter.Convert(null, typeof(object), null, CultureInfo.InvariantCulture));
    }
}
