// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ecl.App.Converters;
using Ecl.App.Localization;
using Ecl.Core.Localization;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 测试进程内唯一的 WPF <see cref="Application"/>。
/// Application 每进程只能创建一次，而 xUnit 会并行运行不同测试类，
/// 因此创建与检查必须在同一把进程级锁内完成；各测试类只通过这里获取。
/// </summary>
internal static class TestApplication
{
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (Gate)
        {
            Ensure();
            action();
        }
    }

    public static void Ensure()
    {
        lock (Gate)
        {
            LocalizationProvider.Current ??= new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

            if (Application.Current is not null)
            {
                return;
            }

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/ECL;component/Themes/Theme.xaml", UriKind.Absolute),
            });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/ECL;component/Themes/Brand.xaml", UriKind.Absolute),
            });

            // 与 App.xaml 保持同一组应用级转换器：页面与弹窗视图在测试环境中也必须能完整加载。
            application.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            application.Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
            application.Resources["StringToVisibility"] = new StringToVisibilityConverter();
            application.Resources["NullToVisibility"] = new NullToVisibilityConverter();
            application.Resources["LogLevelBrush"] = new LogLevelToBrushConverter();
            application.Resources["LoaderKindBrush"] = new LoaderKindToBrushConverter();
        }
    }
}
