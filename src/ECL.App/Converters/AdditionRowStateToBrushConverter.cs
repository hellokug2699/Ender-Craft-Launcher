// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.App.ViewModels.Settings;

namespace Ecl.App.Converters;

/// <summary>
/// 扩展行状态 → 语义色（settings-additions.md §3）。状态同时由文字表达，不只依赖颜色（ADR 0050）：
/// 已启用为成功色、已禁用为弱化色、依赖缺失为警告色、Core API 不兼容为危险色。
/// </summary>
public sealed class AdditionRowStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AdditionRowState.Enabled => Resolve("SuccessBrush", Color.FromRgb(0x3D, 0xDC, 0x97)),
        AdditionRowState.MissingDependency => Resolve("WarningBrush", Color.FromRgb(0xF2, 0xC1, 0x4E)),
        AdditionRowState.Incompatible => Resolve("DangerBrush", Color.FromRgb(0xF0, 0x6A, 0x6A)),
        _ => Resolve("TextMutedBrush", Color.FromRgb(0x6B, 0x74, 0x85)),
    };

    private static Brush Resolve(string resourceKey, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(fallback);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
