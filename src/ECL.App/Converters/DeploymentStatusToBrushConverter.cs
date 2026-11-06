// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.App.ViewModels;

namespace Ecl.App.Converters;

/// <summary>
/// 实例部署项的状态线颜色（`download-instance-versions.md` §10.1）：
/// 已完成绿、正在下载白、正在排队灰、下载失败红；未选择与未开始不画线。
/// </summary>
public sealed class DeploymentStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DeploymentItemStatus.Completed => Resolve("SuccessBrush", Color.FromRgb(0x4B, 0xDA, 0x9F)),
        DeploymentItemStatus.Downloading => Resolve("TextPrimaryBrush", Color.FromRgb(0xF3, 0xF2, 0xF7)),
        DeploymentItemStatus.Queued => Resolve("TextMutedBrush", Color.FromRgb(0x71, 0x6C, 0x82)),
        DeploymentItemStatus.Failed => Resolve("DangerBrush", Color.FromRgb(0xF4, 0x79, 0x7F)),
        _ => Brushes.Transparent,
    };

    private static Brush Resolve(string resourceKey, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(fallback);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
