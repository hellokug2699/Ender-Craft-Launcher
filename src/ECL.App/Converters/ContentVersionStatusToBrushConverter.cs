// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.App.ViewModels;

namespace Ecl.App.Converters;

/// <summary>
/// 版本行条级进度与状态文字的颜色（`download-mods.md` §8）：完成绿、进行中白、排队灰、失败红。
/// 未开始时透明；颜色只作为辅助，状态文字始终同时出现。
/// </summary>
public sealed class ContentVersionStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ContentVersionStatus.Completed => Resolve("SuccessBrush", Color.FromRgb(0x4B, 0xDA, 0x9F)),
        ContentVersionStatus.Downloading => Resolve("TextPrimaryBrush", Color.FromRgb(0xF3, 0xF2, 0xF7)),
        ContentVersionStatus.Queued => Resolve("TextMutedBrush", Color.FromRgb(0x71, 0x6C, 0x82)),
        ContentVersionStatus.Failed => Resolve("DangerBrush", Color.FromRgb(0xF4, 0x79, 0x7F)),
        _ => Brushes.Transparent,
    };

    private static Brush Resolve(string resourceKey, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(fallback);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
