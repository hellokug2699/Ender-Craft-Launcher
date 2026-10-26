// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.Minecraft.Launch;

namespace Ecl.App.Converters;

public sealed class LaunchStatusToBrushConverter : IValueConverter
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E));
    private static readonly Brush FailedBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x6A));
    private static readonly Brush SkippedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x74, 0x85));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LaunchStepStatus.Success => SuccessBrush,
        LaunchStepStatus.Warning => WarningBrush,
        LaunchStepStatus.Failed => FailedBrush,
        _ => SkippedBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
