// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.App.Tests.Shell;

/// <summary>
/// 界面偏好的换算规则与应用逻辑（settings-interface-preferences.md §3–§7）：
/// 字号档位倍率、缩放范围与步进、界面树字号换算与还原、窗口布局缩放、动效偏好、
/// 以及字体族通过主题动态资源生效的默认/覆盖两条分支。
/// </summary>
public sealed class UserInterfacePreferencesTests
{
    [Theory]
    [InlineData("standard", 1.0)]
    [InlineData("large", 1.1)]
    [InlineData("xlarge", 1.25)]
    [InlineData("LARGE", 1.1)]
    public void FontSizeTiersMapToTheDesignSystemFactors(string tier, double expected)
    {
        Assert.Equal(expected, UiTypography.GetFontSizeFactor(tier));
    }

    [Theory]
    [InlineData(null, "standard")]
    [InlineData("", "standard")]
    [InlineData("tiny", "standard")]
    [InlineData("XLARGE", "xlarge")]
    public void UnknownFontSizeTiersFallBackToStandard(string? tier, string expected)
    {
        Assert.Equal(expected, UiTypography.NormalizeFontSizeTier(tier));
        Assert.Equal(
            new[] { "standard", "large", "xlarge" },
            UiTypography.FontSizeTiers);
    }

    [Theory]
    [InlineData(83, 85)]
    [InlineData(84.9, 85)]
    [InlineData(82.4, 80)]
    [InlineData(79, 80)]
    [InlineData(80, 80)]
    [InlineData(150, 150)]
    [InlineData(153, 150)]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(double.NaN, 100)]
    public void UiScaleSnapsToFivePercentStepsInsideTheValidatedRange(double input, int expected)
    {
        Assert.Equal(expected, UiTypography.ClampUiScale(input));
    }

    [Fact]
    public void ScaledFontSizesAreRoundedToTwoDecimals()
    {
        Assert.Equal(16.25, UiTypography.ScaleFontSize(13, 1.25));
        Assert.Equal(27.5, UiTypography.ScaleFontSize(22, 1.25));
        Assert.Equal(10, UiTypography.ScaleFontSize(10, 1.0));
    }

    [Fact]
    public void TextScalerScalesOwnFontSizesAndRestoresTheOriginalLayout()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Run(() =>
            {
                var panel = new StackPanel();
                TextElement.SetFontSize(panel, 20);
                var body = new TextBlock { Text = "body", FontSize = 10 };
                var heading = new TextBlock { Text = "heading", FontSize = 28 };
                // 主题里的隐式 TextBlock 样式会设置字号；这里显式给一个空样式，
                // 让该元素真正“只继承父元素字号”，覆盖 Inherited 分支。
                var inherited = new TextBlock
                {
                    Text = "inherited",
                    Style = new Style(typeof(TextBlock)),
                };
                panel.Children.Add(body);
                panel.Children.Add(heading);
                panel.Children.Add(inherited);

                var root = new Border { Child = panel };
                root.Measure(new Size(400, 400));
                root.Arrange(new Rect(0, 0, 400, 400));
                Assert.Equal(20, inherited.FontSize);

                using var scaler = new UiTextScaler(root);
                scaler.Apply(1.25);

                Assert.Equal(25, TextElement.GetFontSize(panel));
                Assert.Equal(12.5, body.FontSize);
                Assert.Equal(35, heading.FontSize);
                // 纯继承的字号跟随父元素，不被重复乘算。
                Assert.Equal(25, inherited.FontSize);

                scaler.Apply(1.0);

                Assert.Equal(20, TextElement.GetFontSize(panel));
                Assert.Equal(10, body.FontSize);
                Assert.Equal(28, heading.FontSize);
                Assert.Equal(20, inherited.FontSize);

                // 倍率生效后新增的元素在下次应用时参与换算（虚拟化列表/弹窗模板的路径）。
                scaler.Apply(1.25);
                var late = new TextBlock { Text = "late", FontSize = 13 };
                panel.Children.Add(late);
                scaler.Apply(1.25);

                Assert.Equal(16.25, late.FontSize);
            });
        });
    }

    [Fact]
    public void TextScalerNeverTouchesDataBoundFontSizes()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Run(() =>
            {
                var model = new FontSizeModel { Size = 10 };
                var bound = new TextBlock { Text = "bound" };
                bound.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(FontSizeModel.Size))
                {
                    Source = model,
                    Mode = BindingMode.OneWay,
                });

                var root = new StackPanel();
                root.Children.Add(bound);
                root.Measure(new Size(200, 200));

                using var scaler = new UiTextScaler(root);
                scaler.Apply(1.25);

                Assert.True(BindingOperations.IsDataBound(bound, TextBlock.FontSizeProperty));
                Assert.Equal(10, bound.FontSize);
            });
        });
    }

    [Fact]
    public void PreferencesApplyLayoutScaleFontResourcesAndTheMotionPreference()
    {
        using var environment = new PreferencesEnvironment();
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Run(() =>
            {
                using var preferences = new UserInterfacePreferences(environment.Settings);
                var content = new Grid();
                // 与主窗口一致的最小尺寸；缩放后窗口与最小尺寸按同一比例变化。
                var window = new Window
                {
                    Width = 1080,
                    Height = 720,
                    MinWidth = 1080,
                    MinHeight = 720,
                    Content = content,
                };

                try
                {
                    preferences.Attach(window);

                    // 默认 100%：内容不缩放，是恒等变换，窗口尺寸不变。
                    Assert.NotNull(content.LayoutTransform);
                    Assert.True(content.LayoutTransform.Value.IsIdentity);
                    Assert.Equal(1080, window.Width);
                    Assert.Equal(1080, window.MinWidth);

                    environment.Settings.TrySetRaw("launcher.uiScale", "125", null, out var scaleError);
                    Assert.True(scaleError is null, scaleError);
                    Assert.Equal(1.25, ((ScaleTransform)content.LayoutTransform).ScaleX);
                    Assert.Equal(1.25, ((ScaleTransform)content.LayoutTransform).ScaleY);

                    // 布局可用逻辑尺寸保持 1080×720：窗口与最小尺寸也 ×1.25（受屏幕可用范围限制）。
                    var workArea = SystemParameters.WorkArea;
                    Assert.Equal(Math.Min(1350, workArea.Width), window.Width);
                    Assert.Equal(Math.Min(900, workArea.Height), window.Height);
                    Assert.Equal(Math.Min(1350, workArea.Width), window.MinWidth);
                    Assert.Equal(Math.Min(900, workArea.Height), window.MinHeight);

                    environment.Settings.TrySetRaw("launcher.uiScale", "80", null, out _);
                    Assert.Equal(0.8, ((ScaleTransform)content.LayoutTransform).ScaleX);

                    environment.Settings.TrySetRaw("launcher.animationsEnabled", "false", null, out _);
                    Assert.False(preferences.MotionEnabled);
                    Assert.False(MotionSettings.UserEnabled);

                    environment.Settings.TrySetRaw("launcher.animationsEnabled", "true", null, out _);
                    Assert.True(MotionSettings.UserEnabled);

                    // 字体族：默认字体族不覆盖主题资源；其它已登记字体族通过应用级动态资源生效。
                    preferences.ApplyFontFamily(BundledFontFamilies.Default);
                    Assert.False(HasOwnResource(BundledFontFamilies.RegularResourceKey));
                    Assert.False(HasOwnResource(BundledFontFamilies.MediumResourceKey));

                    var alternate = new BundledFontFamily(
                        "test-family",
                        "option.launcher.interfaceFont.harmonyosSc",
                        "Segoe UI",
                        "Segoe UI Semibold");
                    preferences.ApplyFontFamily(alternate);

                    var regular = Assert.IsType<FontFamily>(
                        Application.Current.Resources[BundledFontFamilies.RegularResourceKey]);
                    var medium = Assert.IsType<FontFamily>(
                        Application.Current.Resources[BundledFontFamilies.MediumResourceKey]);
                    // 字体族按家族名打包引用（不能只写家族名，否则拿不到随包字体）。
                    Assert.EndsWith("#Segoe UI", regular.Source);
                    Assert.EndsWith("#Segoe UI Semibold", medium.Source);

                    preferences.ApplyFontFamily(BundledFontFamilies.Default);
                    Assert.False(HasOwnResource(BundledFontFamilies.RegularResourceKey));
                }
                finally
                {
                    MotionSettings.UserEnabled = true;
                }
            });
        });
    }

    /// <summary>只检查应用级字典自身的条目；主题里的同键资源来自合并字典，不在这里。</summary>
    private static bool HasOwnResource(string key) =>
        Application.Current!.Resources.Keys
            .Cast<object>()
            .Any(candidate => string.Equals(candidate as string, key, StringComparison.Ordinal));

    [Fact]
    public void ScalingRoundTripRestoresTheWindowSizeEvenWhenTheScreenClampsIt()
    {
        using var environment = new PreferencesEnvironment();
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Run(() =>
            {
                using var preferences = new UserInterfacePreferences(environment.Settings);
                var window = new Window
                {
                    Width = 1600,
                    Height = 950,
                    MinWidth = 1080,
                    MinHeight = 720,
                    Content = new Grid(),
                };

                try
                {
                    preferences.Attach(window);
                    var workArea = SystemParameters.WorkArea;

                    environment.Settings.TrySetRaw("launcher.uiScale", "150", null, out _);
                    Assert.Equal(Math.Min(2400, workArea.Width), window.Width);

                    // 150% 可能被屏幕可用范围夹住；回到 100% 必须恢复用户原来的窗口尺寸，
                    // 而不是把夹过之后的尺寸当成新基准（实机复现过窗口越缩越小）。
                    environment.Settings.TrySetRaw("launcher.uiScale", "100", null, out _);
                    Assert.Equal(1600, window.Width);
                    Assert.Equal(950, window.Height);
                    Assert.Equal(1080, window.MinWidth);
                    Assert.Equal(720, window.MinHeight);
                }
                finally
                {
                    MotionSettings.UserEnabled = true;
                }
            });
        });
    }

    private sealed class FontSizeModel
    {
        public double Size { get; init; }
    }

    private sealed class PreferencesEnvironment : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public PreferencesEnvironment()
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-preferences-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<ISettingsService>();
        }

        public ISettingsService Settings { get; }

        public void Dispose()
        {
            _provider.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不应影响测试结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
