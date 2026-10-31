// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

/// <summary>
/// 只为第三方（Authlib-Injector）账户准备客户端注入参数。
/// Microsoft 与离线账户完全不走这里，保持原有启动路径不变。
/// </summary>
internal sealed class AuthlibInjectorStep(
    IAuthlibInjectorService injector,
    IAuthServerService authServers,
    ILocalizer localizer,
    ILogger<AuthlibInjectorStep> logger) : ILaunchStep
{
    public string Id => "authlib-injector";

    public int Order => 380;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.Account.Type != AccountType.ThirdParty)
        {
            return new LaunchStepResult(Id, LaunchStepStatus.Skipped, localizer.Get("launch.injector.notNeeded"));
        }

        var serverId = context.Account.AuthServerId;
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return new LaunchStepResult(Id, LaunchStepStatus.Failed, localizer.Get("launch.injector.missingServer"));
        }

        var server = await authServers.GetAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (server is null)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.injector.missingServer"));
        }

        if (context.DryRun)
        {
            var cached = injector.TryGetCachedJarPath() ?? "<authlib-injector.jar>";
            context.JavaAgentArgument = $"-javaagent:{cached}={server.Url}";
            return new LaunchStepResult(Id, LaunchStepStatus.Skipped, localizer.Get("launch.injector.dryRun"));
        }

        var artifact = await injector.EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!artifact.IsSuccess)
        {
            logger.LogError("准备 Authlib-Injector 失败：{Code}", artifact.Error?.Code);
            return new LaunchStepResult(Id, LaunchStepStatus.Failed, artifact.Error!.Message);
        }

        context.JavaAgentArgument = $"-javaagent:{artifact.Value!.Path}={server.Url}";
        logger.LogInformation(
            "已为实例 {InstanceId} 注入 Authlib-Injector {Version}（{Server}）。",
            context.Instance.Id,
            artifact.Value.Version,
            server.Name);

        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.injector.ready", artifact.Value.Version, server.Name));
    }
}
