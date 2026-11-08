// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ecl.App.Controls;
using Ecl.App.Converters;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
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

    [Fact]
    public void DisabledListBoxKeepsTheThemeBackground()
    {
        byte[] pixel = [];

        using (var ui = new UiThread())
        {
            ui.Invoke(() =>
            {
                lock (ApplicationGate)
                {
                    EnsureApplication();

                    // 弹窗打开会禁用整个 Shell。WPF 默认 ListBox 模板在禁用时改用
                    // SystemColors.ControlBrush（浅色），会让深色页面整块发白；
                    // 主题模板必须保持深色背景。
                    var host = new Grid
                    {
                        Width = 200,
                        Height = 100,
                        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x1F)),
                    };
                    var list = new ListBox
                    {
                        Width = 200,
                        Height = 100,
                        IsEnabled = false,
                        ItemsSource = Array.Empty<string>(),
                    };
                    host.Children.Add(list);
                    host.Measure(new Size(200, 100));
                    host.Arrange(new Rect(0, 0, 200, 100));
                    host.UpdateLayout();

                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        200,
                        100,
                        96,
                        96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(host);

                    pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(20, 80, 1, 1), pixel, 4, 0);
                }
            });
        }

        Assert.Equal(0xFF, pixel[3]);
        Assert.True(
            pixel[0] < 0x50 && pixel[1] < 0x50 && pixel[2] < 0x50,
            $"禁用列表的背景不应变成系统浅色：B={pixel[0]} G={pixel[1]} R={pixel[2]}");
    }

    [Fact]
    public void ModStatusBadgePanelArrangesOneTwoThreeAndFourIcons()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            lock (ApplicationGate)
            {
                EnsureApplication();

                Assert.Equal(new[] { new Vector(8, 8) }, Arrange(1));
                Assert.Equal(new[] { new Vector(0, 8), new Vector(16, 8) }, Arrange(2));
                Assert.Equal(new[] { new Vector(8, 0), new Vector(0, 16), new Vector(16, 16) }, Arrange(3));
                Assert.Equal(
                    new[] { new Vector(0, 0), new Vector(16, 0), new Vector(0, 16), new Vector(16, 16) },
                    Arrange(4));
            }
        });
    }

    /// <summary>在 30×30 单元格内测量/排列 1–4 个 14px 图标，返回每个图标的相对偏移。</summary>
    private static Vector[] Arrange(int count)
    {
        var panel = new ModStatusBadgePanel { IconSize = 14, Gap = 2 };
        for (var index = 0; index < count; index++)
        {
            panel.Children.Add(new ModStatusGlyph { Kind = ModStatusKind.Available });
        }

        panel.Measure(new Size(30, 30));
        panel.Arrange(new Rect(0, 0, 30, 30));

        return panel.Children
            .Cast<UIElement>()
            .Select(VisualTreeHelper.GetOffset)
            .ToArray();
    }

    private static void EnsureApplication() => TestApplication.Ensure();
}


