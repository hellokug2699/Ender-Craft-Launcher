// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.Java;

namespace Ecl.App.Converters;

/// <summary>
/// Java 验证状态 → 语义色（java-management.md §3.2.5）。
/// 可用为成功色、验证中为强调色、32 位为警告色、异常为危险色、信息未知为弱化色；
/// 状态同时由文字表达，不只依赖颜色（ADR 0050）。
/// </summary>
public sealed class JavaValidationStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        JavaRuntimeValidationState.Available => Resolve("SuccessBrush", Color.FromRgb(0x3D, 0xDC, 0x97)),
        JavaRuntimeValidationState.Validating => Resolve("AccentBrush", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        JavaRuntimeValidationState.Unsupported32Bit => Resolve("WarningBrush", Color.FromRgb(0xF2, 0xC1, 0x4E)),
        JavaRuntimeValidationState.Failed => Resolve("DangerBrush", Color.FromRgb(0xF0, 0x6A, 0x6A)),
        _ => Resolve("TextMutedBrush", Color.FromRgb(0x6B, 0x74, 0x85)),
    };

    private static Brush Resolve(string resourceKey, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(fallback);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
