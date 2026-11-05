// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Ecl.App.Converters;

/// <summary>
/// 加载器类型 → 语义颜色（instances.md §3.2）：
/// Forge 红、NeoForge 橙、Fabric 蓝、Cleanroom 绿、未知灰。
/// 颜色从主题令牌解析，页面不直接写死颜色值。
/// </summary>
public sealed class LoaderKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var resourceKey = (value as string) switch
        {
            "forge" => "LoaderForgeBrush",
            "neoforge" => "LoaderNeoForgeBrush",
            "fabric" => "LoaderFabricBrush",
            "cleanroom" => "LoaderCleanroomBrush",
            _ => "LoaderUnknownBrush",
        };

        return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
