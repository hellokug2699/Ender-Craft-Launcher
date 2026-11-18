// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;

namespace Ecl.App.Services.Confirmation;

/// <summary>
/// 结果弹窗的默认实现：复用 <see cref="IDialogEngine"/> 与既有 <see cref="MessageDialogViewModel"/>，
/// 不新增 XAML 或 ViewModel（内容由 UI 切片扩展）。
///
/// 自动关闭策略只对“非关键”结果生效，并且需要用户显式开启
/// <c>launcher.autoCloseSuccessDialogs</c>。自动关闭以 <see cref="DialogOutcome.Closed"/> 结束，
/// 不伪造 <see cref="DialogOutcome.Primary"/>：窗口自己关掉不等于用户确认。
///
/// 弹窗宿主尚未附加时（首次启动向导、启动失败）回退到 <see cref="IDialogService.ShowInfo"/>，
/// 并返回 <see cref="DialogResult.Closed"/>——没读到用户选择就不要假装读到了。
/// </summary>
public sealed class ResultDialogService : IResultDialogService
{
    /// <summary>成功结果的展示时长；足够读完一行结论，又不会长期占住界面。</summary>
    public static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(8);

    private readonly ISettingsService _settings;
    private readonly IDialogEngine _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IDialogService? _fallback;

    public ResultDialogService(
        ISettingsService settings,
        IDialogEngine dialogs,
        ILocalizer localizer,
        IDialogService? fallbackDialogs = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _fallback = fallbackDialogs;
    }

    public async Task<DialogResult> ShowResultAsync(
        ResultDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var autoCloseAfter = ResolveAutoCloseAfter(request);
        if (_dialogs.IsAvailable)
        {
            try
            {
                var dialogRequest = new DialogRequest
                {
                    Title = request.Title,
                    Content = new MessageDialogViewModel(
                        request.Message,
                        _localizer.Get("dialog.ok"),
                        severity: request.IsCritical ? DialogSeverity.Error : DialogSeverity.Information),
                    InitialFocus = DialogInitialFocus.Primary,
                    Severity = request.IsCritical ? DialogSeverity.Error : DialogSeverity.Information,
                    AutoCloseAfter = autoCloseAfter,
                };

                return await _dialogs.ShowAsync(dialogRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (DialogHostUnavailableException)
            {
                // 宿主在检查与显示之间被卸载：继续走兼容路径，不能把结论静默吞掉。
            }
        }

        _fallback?.ShowInfo(request.Title, request.Message);
        return DialogResult.Closed;
    }

    /// <summary>
    /// 只对非关键结果启用自动关闭；关键结果与未开启偏好时返回 null（等待用户）。
    /// 该判断不读取任务结果，调用方必须显式传 <see cref="ResultDialogRequest.IsCritical"/>。
    /// </summary>
    private TimeSpan? ResolveAutoCloseAfter(ResultDialogRequest request)
    {
        if (request.IsCritical)
        {
            return null;
        }

        return _settings.Get<bool>(LauncherKeys.AutoCloseSuccessDialogs) ? AutoCloseDelay : null;
    }
}
