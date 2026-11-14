// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Ecl.App.Converters;

/// <summary>
/// 存储分布条与图例共用同一主题画刷。View 模型只提供语义 Key，颜色不进入业务状态。
/// </summary>
public sealed class StorageSegmentBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        return string.IsNullOrWhiteSpace(key)
            ? Brushes.Gray
            : System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
