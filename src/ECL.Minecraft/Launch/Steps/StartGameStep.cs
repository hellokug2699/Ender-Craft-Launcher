// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class StartGameStep(
    IProcessLauncher processLauncher,
    IGameSessionService sessions,
    ILocalizer localizer,
    ILogger<StartGameStep> logger) : ILaunchStep
{
    public string Id => "start";

    public int Order => 500;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Skipped,
                localizer.Get("launch.start.dryRun")));
        }

        if (context.Command is null)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.start.noCommand")));
        }

        var showConsole = context.Settings.Get<bool>("game.showConsole");
        string? sessionId = null;

        try
        {
            // ADR 0015：进程启动之前先持久化会话，保证异常退出后仍可追溯。
            var session = sessions.Create(new SessionCreateRequest
            {
                InstanceId = context.Instance.Id,
                InstanceName = context.Instance.Name,
                GameDirectory = context.GameDirectory,
                JavaPath = context.JavaPlan?.ExecutablePath ?? context.JavaExecutable,
                JavaVersion = context.JavaPlan?.Version,
                MinecraftVersion = context.Instance.GameVersion,
                Loader = context.Instance.Loader,
                LogIsolation = showConsole ? "unavailable" : "direct",
            });

            sessionId = session.SessionId;
            context.SessionId = sessionId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "创建游戏日志会话失败。");
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                $"无法创建日志会话：{exception.Message}"));
        }

        try
        {
            var processId = processLauncher.Start(
                context.Command,
                context.GameDirectory,
                showConsole,
                context.Instance.Id,
                sessionId);
            context.ProcessId = processId;

            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.start.started", processId)));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "启动实例 {InstanceId} 的进程失败。", context.Instance.Id);
            if (sessionId is not null)
            {
                sessions.MarkState(sessionId, GameSessionState.Interrupted, exception.Message);
            }

            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.start.failed", exception.Message)));
        }
    }
}
