// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;

namespace Ecl.App.Converters;

/// <summary>布尔取反；用于 IsEnabled 等需要与 IsBusy 相反的属性（可见性请用现有可见性转换器）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : true;
}
