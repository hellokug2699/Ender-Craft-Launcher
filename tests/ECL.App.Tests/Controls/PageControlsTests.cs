// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ecl.App.Controls;
using Ecl.App.Converters;
using Ecl.App.Localization;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Controls;

/// <summary>
/// B0 共享控件必须在真实主题资源下可加载：令牌缺失、样式名写错或可见性计算错误
/// 都会在页面第一次使用前暴露，而不是推迟到 B1。
/// </summary>
public sealed class PageControlsTests
{
    private static readonly object ApplicationGate = new();

    [Fact]
    public void HeaderComputesTitleAndVisibility()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            lock (ApplicationGate)
            {
                EnsureApplication();

                var header = new PageHeader { Title = "实例", Subtitle = "全部已发现实例" };
                Assert.Equal("实例", header.Title);
                Assert.Equal(Visibility.Visible, header.SubtitleVisibility);
                Assert.Equal(Visibility.Collapsed, header.ActionsVisibility);

                header.Actions = new object();
                Assert.Equal(Visibility.Visible, header.ActionsVisibility);

                header.Subtitle = "   ";
                Assert.Equal(Visibility.Collapsed, header.SubtitleVisibility);
            }
        });
    }

    [Fact]
    public void StateViewUsesLocalizedDefaultsAndOverrides()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            lock (ApplicationGate)
            {
                EnsureApplication();

                var state = new PageStateView { State = PageState.Unavailable };
                Assert.Equal("当前不可用", state.StateLabel);
                Assert.Equal("该能力当前不可用", state.DisplayTitle);
                Assert.Equal(Visibility.Visible, state.DescriptionVisibility);
                Assert.Equal(Visibility.Collapsed, state.ActionVisibility);

                state.Description = "缺少 NBT Explorer Addition。";
                Assert.Equal("缺少 NBT Explorer Addition。", state.DisplayDescription);

                state.State = PageState.Loading;
                Assert.Equal("加载中", state.StateLabel);

                state.ActionContent = new object();
                Assert.Equal(Visibility.Visible, state.ActionVisibility);
            }
        });
    }

    [Fact]
    public void LoaderKindsResolveToDistinctThemeBrushes()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            lock (ApplicationGate)
            {
                EnsureApplication();

                var converter = new LoaderKindToBrushConverter();
                var brushes = new[] { "forge", "neoforge", "fabric", "cleanroom", "unknown" }
                    .Select(kind => converter.Convert(kind, typeof(Brush), null, CultureInfo.InvariantCulture))
                    .Cast<Brush>()
                    .ToArray();

                Assert.All(brushes, brush => Assert.NotSame(Brushes.Transparent, brush));
                Assert.Equal(5, brushes.Distinct().Count());
            }
        });
    }

    [Fact]
    public void InstanceDirectoryDialogViewLoadsFromTheme()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            lock (ApplicationGate)
            {
                EnsureApplication();

                var view = new Ecl.App.Dialogs.Views.InstanceDirectoryDialogView();

                Assert.NotNull(view.Content);
            }
        });
    }

    private static void EnsureApplication()
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
