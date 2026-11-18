// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Deployment.Startup;

/// <summary>
/// 让 Windows 登录启动项与 <c>launcher.startWithWindows</c> 保持一致。
///
/// 只做“读取设置 → 对齐注册表”，**从不改写用户设置**：注册表写入失败时
/// 用户的意图（开/关）保持不变，失败通过返回值与日志暴露，由界面告知用户重试。
/// 因此本方法可以安全地在每次启动时幂等调用，也适合用户在设置页切换开关后调用。
/// </summary>
public sealed class AutoStartReconciler(
    IAutoStartRegistration registration,
    ISettingsService settings,
    ILogger<AutoStartReconciler> logger)
{
    public Result Reconcile()
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(settings);

        var desired = settings.Get<bool>(LauncherKeys.StartWithWindows);

        AutoStartState state;
        try
        {
            state = registration.Read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 读取异常也应可见：否则界面会把“读不到”当成“未启用”并写坏注册表。
            logger.LogWarning(exception, "读取登录启动项状态失败。");
            return Result.Fail("autostart.read_failed", "无法读取 Windows 登录启动项。", exception.Message);
        }

        if (!state.IsSupported)
        {
            // 不支持不是错误路径：开发构建、dotnet 宿主都会走到这里，
            // 用 Information 记录，避免每次启动都产生告警噪声。
            logger.LogInformation("登录启动不可用：{Code} {Error}", state.ErrorCode, state.Error);
            return Result.Fail(
                state.ErrorCode ?? "autostart.unsupported",
                "当前运行方式不支持注册 Windows 登录启动项。",
                state.Error);
        }

        if (state.ErrorCode is not null)
        {
            logger.LogWarning("登录启动项状态不可用：{Code} {Error}", state.ErrorCode, state.Error);
            return Result.Fail(state.ErrorCode, "无法读取 Windows 登录启动项。", state.Error);
        }

        var matches = desired
            ? string.Equals(state.Command, registration.ExpectedCommand, StringComparison.OrdinalIgnoreCase)
            : !state.IsEnabled;
        if (matches)
        {
            return Result.Ok();
        }

        // 修复动作只影响注册表；即使失败也不写 launcher.startWithWindows。
        var applied = registration.SetEnabled(desired);
        if (!applied.IsSuccess)
        {
            logger.LogWarning(
                "对齐登录启动项失败（期望 {Desired}）：{Code} {Message}",
                desired,
                applied.Error!.Code,
                applied.Error.Message);
            return applied;
        }

        logger.LogInformation("已按设置重新应用登录启动项：{Enabled}", desired);
        return Result.Ok();
    }
}
