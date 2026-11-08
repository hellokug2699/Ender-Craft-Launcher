// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ecl.Configuration.Settings;

namespace Ecl.App.Services;

/// <summary>
/// 界面偏好的生效状态（<c>settings-interface-preferences.md</c> §3–§7）。
///
/// 偏好本身由 <see cref="ISettingsService"/> 持久化（ADR 0002），这里只负责把当前值
/// 应用到已经显示的主窗口：
/// - 字体族：覆盖主题的 <c>UiFontFamily</c> / <c>UiMediumFontFamily</c> 动态资源；
/// - 字号档位：由 <see cref="UiTextScaler"/> 按倍率换算界面树上的字号；
/// - 界面缩放：对窗口内容应用 <see cref="LayoutTransform"/>，重新测量而不是裁切；
/// - 动效开关：写入 <see cref="MotionSettings.UserEnabled"/>，操作系统“减少动画”仍是上限。
///
/// 页面不直接调用这里；设置保存成功后由 <see cref="ISettingsService.Changed"/> 触发，
/// 因此 CLI 或其它入口修改同一键也会生效。
/// </summary>
public interface IUserInterfacePreferences
{
    event EventHandler? Changed;

    BundledFontFamily FontFamily { get; }

    string FontSizeTier { get; }

    double FontSizeFactor { get; }

    int UiScalePercent { get; }

    bool MotionEnabled { get; }

    /// <summary>把偏好应用到主窗口；重复调用只重新应用一次，不重复订阅。</summary>
    void Attach(Window window);

    /// <summary>重新读取设置并应用（设置变更时由本类自动调用，测试可直接调用）。</summary>
    void Refresh();
}

public sealed class UserInterfacePreferences : IUserInterfacePreferences, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Dispatcher? _dispatcher;

    private Window? _window;
    private FrameworkElement? _content;
    private UiTextScaler? _scaler;
    private double _appliedLayoutScale = double.NaN;
    private double _baseMinWidth;
    private double _baseMinHeight;
    private double _logicalWidth = double.NaN;
    private double _logicalHeight = double.NaN;
    private bool _previousApplyWasClamped;
    private bool _disposed;

    public UserInterfacePreferences(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    public BundledFontFamily FontFamily =>
        BundledFontFamilies.Resolve(_settings.Get<string>(UiTypography.FontFamilyKey));

    public string FontSizeTier =>
        UiTypography.NormalizeFontSizeTier(_settings.Get<string>(UiTypography.FontSizeKey));

    public double FontSizeFactor => UiTypography.GetFontSizeFactor(FontSizeTier);

    public int UiScalePercent => UiTypography.ClampUiScale(_settings.Get<int>(UiTypography.UiScaleKey));

    public bool MotionEnabled => _settings.Get<bool>(UiTypography.MotionKey);

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_disposed)
        {
            return;
        }

        if (ReferenceEquals(_window, window))
        {
            Refresh();
            return;
        }

        _scaler?.Dispose();
        _window = window;
        _content = window.Content as FrameworkElement;
        // 记录 100% 时的窗口尺寸与最小尺寸：缩放后窗口与最小尺寸按同一比例变化，
        // 保证任何档位下布局可用宽度都不低于设计系统的最小窗口（ADR 0050 §6）。
        _baseMinWidth = window.MinWidth;
        _baseMinHeight = window.MinHeight;
        _logicalWidth = window.Width;
        _logicalHeight = window.Height;
        _previousApplyWasClamped = false;
        // 以窗口为遍历起点：窗口自身的默认字号也要参与换算，否则未显式设置字号
        // 的纯继承元素在“较大/最大”档位下不会变大。
        _scaler = new UiTextScaler(window);
        _appliedLayoutScale = double.NaN;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        // 动效与窗口无关，即使在测试或窗口创建前也应反映当前偏好。
        MotionSettings.UserEnabled = MotionEnabled;

        if (_window is not null)
        {
            ApplyFontFamily();
            ApplyLayoutScale();
            _scaler?.Apply(FontSizeFactor);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _scaler?.Dispose();
        _scaler = null;
        _window = null;
        _content = null;
    }

    private static bool IsPreferenceKey(string key) =>
        string.Equals(key, UiTypography.FontFamilyKey, StringComparison.Ordinal) ||
        string.Equals(key, UiTypography.FontSizeKey, StringComparison.Ordinal) ||
        string.Equals(key, UiTypography.UiScaleKey, StringComparison.Ordinal) ||
        string.Equals(key, UiTypography.MotionKey, StringComparison.Ordinal);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_disposed || e.InstanceId is not null || !IsPreferenceKey(e.Key))
        {
            return;
        }

        // 设置服务在调用方线程上触发事件：CLI/后台线程写入时回到 UI 线程再应用。
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }

        Refresh();
    }

    /// <summary>
    /// 字体族只通过主题动态资源生效：所有字体声明都用 <c>DynamicResource</c> 引用
    /// <c>UiFontFamily</c> / <c>UiMediumFontFamily</c>，这里覆盖或移除应用级覆盖即可。
    /// 选择默认字体族时移除覆盖，保持主题资源原样。
    /// </summary>
    private void ApplyFontFamily() => ApplyFontFamily(FontFamily);

    internal void ApplyFontFamily(BundledFontFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        if (string.Equals(family.Id, BundledFontFamilies.Default.Id, StringComparison.OrdinalIgnoreCase))
        {
            resources.Remove(BundledFontFamilies.RegularResourceKey);
            resources.Remove(BundledFontFamilies.MediumResourceKey);
            return;
        }

        resources[BundledFontFamilies.RegularResourceKey] = family.RegularFontFamily;
        resources[BundledFontFamilies.MediumResourceKey] = family.MediumFontFamily;
    }

    /// <summary>
    /// 界面缩放通过 LayoutTransform 实现：WPF 按缩放后的可用尺寸重新测量，
    /// 页面因此走既有的窄窗口响应式逻辑（折叠导航、表格内部横向滚动），而不是裁切内容。
    ///
    /// 窗口与最小尺寸按同一比例调整：100% 时窗口 1400×880 / 最小 1080×720，
    /// 125% 时窗口与最小尺寸都 ×1.25，布局的可用逻辑尺寸保持不变；否则在最小窗口下
    /// 放大到 125% 只剩 ~860 逻辑像素宽，标题与操作按钮会重叠（实机复现过）。
    /// 窗口最终尺寸受屏幕可用范围限制；受限时页面仍按自身滚动容器收缩，不裁切内容。
    /// Windows 显示缩放、分辨率与系统字体不受影响。
    /// </summary>
    private void ApplyLayoutScale()
    {
        if (_content is null || _window is null)
        {
            return;
        }

        var scale = UiScalePercent / 100.0;
        if (Math.Abs(scale - _appliedLayoutScale) < 0.0001)
        {
            return;
        }

        var previous = double.IsNaN(_appliedLayoutScale) ? 1.0 : _appliedLayoutScale;
        if (double.IsNaN(_logicalWidth) || double.IsNaN(_logicalHeight))
        {
            _logicalWidth = _window.Width / previous;
            _logicalHeight = _window.Height / previous;
        }
        else if (!_previousApplyWasClamped &&
                 _window.WindowState != WindowState.Maximized)
        {
            // 用户可能在上一个比例下手动改过窗口尺寸：按该比例折算回 100% 的等效尺寸。
            // 上一个尺寸若被屏幕可用范围夹过，则保留原基准，避免窗口越缩越小。
            _logicalWidth = _window.Width / previous;
            _logicalHeight = _window.Height / previous;
        }

        _content.LayoutTransform = Math.Abs(scale - 1.0) < 0.0001
            ? Transform.Identity
            : new ScaleTransform(scale, scale);
        _appliedLayoutScale = scale;

        var workArea = SystemParameters.WorkArea;
        _window.MinWidth = Math.Min(_baseMinWidth * scale, workArea.Width);
        _window.MinHeight = Math.Min(_baseMinHeight * scale, workArea.Height);

        // 最大化时窗口尺寸由系统决定，只缩放内容。
        if (_window.WindowState == WindowState.Maximized)
        {
            _previousApplyWasClamped = false;
            return;
        }

        var rawWidth = _logicalWidth * scale;
        var rawHeight = _logicalHeight * scale;
        _window.Width = Math.Max(Math.Min(rawWidth, workArea.Width), _window.MinWidth);
        _window.Height = Math.Max(Math.Min(rawHeight, workArea.Height), _window.MinHeight);
        _previousApplyWasClamped =
            Math.Abs(_window.Width - rawWidth) > 0.5 ||
            Math.Abs(_window.Height - rawHeight) > 0.5;
    }
}
