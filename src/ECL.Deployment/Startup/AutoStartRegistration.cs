// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Deployment.Startup;

/// <summary>
/// 登录启动项的真实状态。<see cref="IsSupported"/> 为 false 时其余字段不可信：
/// 平台或运行方式根本无法注册，界面必须显示原因而不是假装已启用。
/// </summary>
public sealed record AutoStartState(
    bool IsEnabled,
    string? Command,
    bool IsSupported,
    string? ErrorCode,
    string? Error);

/// <summary>
/// 登录启动注册的领域入口。读取只观测注册表，写入只影响 HKCU Run，
/// 两者都不修改 <c>launcher.startWithWindows</c>：设置是用户意图，注册表是它的结果。
/// </summary>
public interface IAutoStartRegistration
{
    /// <summary>注册成功时应写入的命令行；不支持时返回 null。</summary>
    string? ExpectedCommand { get; }

    AutoStartState Read();

    Result SetEnabled(bool enabled);
}

/// <summary>
/// 把“是否随 Windows 启动”落到 <c>HKCU\...\Run\EnderCraftLauncher</c>。
///
/// 命令固定为 <c>"&lt;exe&gt;" --autostart</c>：带引号保证路径中的空格不会把参数拆开，
/// <c>--autostart</c> 让启动后的进程知道“这次可以最小化到托盘”（由 UI 宿主解释）。
/// 写入失败时保持注册表原值不变，不会先删除再写，避免把用户已有的启动项弄丢。
/// </summary>
public sealed class AutoStartRegistration(
    IRunKeyStore store,
    IExecutableLocator locator,
    ILogger<AutoStartRegistration> logger) : IAutoStartRegistration
{
    public const string AutoStartArgument = "--autostart";

    public string? ExpectedCommand
    {
        get
        {
            var executable = locator.ResolveStartupExecutable();
            return string.IsNullOrWhiteSpace(executable)
                ? null
                : $"\"{executable}\" {AutoStartArgument}";
        }
    }

    public AutoStartState Read()
    {
        var expected = ExpectedCommand;
        if (expected is null)
        {
            return Unsupported();
        }

        if (!store.TryRead(RunKeyNames.ValueName, out var command, out var error))
        {
            logger.LogWarning("读取登录启动项失败：{Error}", error);
            return new AutoStartState(false, null, true, "autostart.read_failed", error);
        }

        // 值存在即视为“已注册”；命令是否与期望一致由 Reconcile 判断，
        // 这样界面可以显示真实命令（例如用户把安装目录搬走后留下的旧路径）。
        return new AutoStartState(!string.IsNullOrWhiteSpace(command), command, true, null, null);
    }

    public Result SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (!store.TryDelete(RunKeyNames.ValueName, out var deleteError))
            {
                logger.LogWarning("移除登录启动项失败：{Error}", deleteError);
                return Result.Fail("autostart.registry_failed", "无法移除 Windows 登录启动项。", deleteError);
            }

            // 值本来就不存在也算成功：重复关闭自动启动不应报错。
            return Result.Ok();
        }

        var command = ExpectedCommand;
        if (command is null)
        {
            logger.LogInformation("当前运行方式不支持登录启动注册，跳过写入。");
            return Result.Fail("autostart.unsupported", "当前运行方式不支持注册 Windows 登录启动项。");
        }

        if (!store.TryWrite(RunKeyNames.ValueName, command, out var writeError))
        {
            logger.LogWarning("写入登录启动项失败：{Error}", writeError);
            return Result.Fail("autostart.registry_failed", "无法写入 Windows 登录启动项。", writeError);
        }

        logger.LogInformation("已写入登录启动项：{Command}", command);
        return Result.Ok();
    }

    private static AutoStartState Unsupported() =>
        new(false, null, false, "autostart.unsupported", "当前运行方式不支持注册 Windows 登录启动项。");
}
