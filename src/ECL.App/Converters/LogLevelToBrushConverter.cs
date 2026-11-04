// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Converters;

/// <summary>把日志级别映射到设计系统中的状态色；不在 XAML 里硬编码颜色。</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LogLevel.Warning => "WarningBrush",
            LogLevel.Error or LogLevel.Critical => "DangerBrush",
            LogLevel.Debug or LogLevel.Trace => "TextMutedBrush",
            _ => "TextSecondaryBrush",
        };

        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
