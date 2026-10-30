// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ecl.App;

/// <summary>
/// Bundled ECL branding assets. The compact icon is used by the desktop window/taskbar;
/// the full mark is used by the startup splash screen.
/// </summary>
internal static class BrandAssets
{
    private const string IconBase64 = "[ICON_BASE64_PLACEHOLDER]";
    private const string SplashBase64 = "[SPLASH_BASE64_PLACEHOLDER]";

    public static ImageSource CreateIcon() => Load(IconBase64);

    public static ImageSource CreateSplash() => Load(SplashBase64);

    private static BitmapImage Load(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
