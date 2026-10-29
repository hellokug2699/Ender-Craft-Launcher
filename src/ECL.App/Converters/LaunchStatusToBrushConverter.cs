// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Ecl.Minecraft.Launch;

namespace Ecl.App.Converters;

public sealed class LaunchStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LaunchStepStatus.Success => Resolve("SuccessBrush", Color.FromRgb(0x3D, 0xDC, 0x97)),
        LaunchStepStatus.Warning => Resolve("WarningBrush", Color.FromRgb(0xF2, 0xC1, 0x4E)),
        LaunchStepStatus.Failed => Resolve("DangerBrush", Color.FromRgb(0xF0, 0x6A, 0x6A)),
        _ => Resolve("TextMutedBrush", Color.FromRgb(0x6B, 0x74, 0x85)),
    };

    private static Brush Resolve(string resourceKey, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(fallback);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
