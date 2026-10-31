// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public interface ILaunchService
{
    Task<LaunchReport> LaunchAsync(string instanceId, bool dryRun, CancellationToken cancellationToken = default);
}

public sealed class LaunchService : ILaunchService
{
    /// <summary>启动前提前多久续期：只有剩余有效期不足这个值时才静默刷新。</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(30);

    private readonly IInstanceService _instances;
    private readonly IAccountService _accounts;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IReadOnlyList<ILaunchStep> _steps;
    private readonly ILocalizer _localizer;
    private readonly ILogger<LaunchService> _logger;

    public LaunchService(
        IInstanceService instances,
        IAccountService accounts,
        ISettingsService settings,
        IAppPaths paths,
        IEnumerable<ILaunchStep> steps,
        ILocalizer localizer,
        ILogger<LaunchService> logger)
    {
        _instances = instances;
        _accounts = accounts;
        _settings = settings;
        _paths = paths;
        _steps = steps.OrderBy(step => step.Order).ToArray();
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<LaunchReport> LaunchAsync(string instanceId, bool dryRun, CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();

        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Failure("instance", _localizer.Get("launch.instance.invalid"), dryRun, total);
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Failure("instance", _localizer.Get("launch.instance.missing", instanceId), dryRun, total);
        }

        var account = await _accounts.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account is null && !dryRun)
        {
            return Failure("account", _localizer.Get("launch.account.missing"), dryRun, total);
        }

        // Health checks validate the machine/game pipeline, not the user's identity.
        // A synthetic offline identity is used only to construct a dry-run command;
        // real launches still require a configured account.
        account ??= new Account
        {
            Id = "ecl-health-check",
            Type = AccountType.Offline,
            Name = "ECLHealthCheck",
            Uuid = OfflineUuid.Create("ECLHealthCheck"),
        };

        if (!dryRun && account.Type == AccountType.Microsoft)
        {
            // 正常启动器不会为了续期打开浏览器：refresh token 静默换新，
            // 只有 refresh token 本身失效才需要用户重新登录。
            var clientId = _settings.Get<string>("auth.microsoftClientId")?.Trim();
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var refreshed = await _accounts
                    .EnsureFreshAsync(
                        account,
                        new MicrosoftAuthenticationOptions { ClientId = clientId },
                        RefreshMargin,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!refreshed.IsSuccess)
                {
                    return Failure(
                        "account",
                        $"Microsoft 账户凭据刷新失败：{refreshed.Error?.Message}",
                        dryRun,
                        total);
                }

                account = refreshed.Value!;
            }
        }

        if (!dryRun && account.Type == AccountType.Microsoft && string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return Failure("account", "Microsoft 账户登录凭据不可用，请重新登录。", dryRun, total);
        }

        var context = new LaunchContext
        {
            Instance = instance,
            Account = account,
            Paths = _paths,
            Settings = _settings,
            DryRun = dryRun,
        };

        var results = new List<LaunchStepResult>();
        var success = true;

        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepTimer = Stopwatch.StartNew();
            LaunchStepResult result;

            try
            {
                result = await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "启动步骤 {StepId} 执行失败。", step.Id);
                result = new LaunchStepResult(
                    step.Id,
                    LaunchStepStatus.Failed,
                    _localizer.Get("launch.step.exception", exception.Message));
            }

            stepTimer.Stop();
            result = result with { Duration = stepTimer.Elapsed };
            results.Add(result);

            if (result.Status == LaunchStepStatus.Failed)
            {
                success = false;
                break;
            }
        }

        if (success && !dryRun)
        {
            await _instances.TouchLastPlayedAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        }

        total.Stop();
        _logger.LogInformation(
            "实例 {InstanceId} 启动流程结束：成功={Success}，DryRun={DryRun}，耗时={Duration}。",
            instance.Id,
            success,
            dryRun,
            total.Elapsed);

        return new LaunchReport(
            success,
            dryRun,
            context.Command?.Display,
            context.ProcessId,
            results,
            total.Elapsed);
    }

    private static LaunchReport Failure(string stepId, string message, bool dryRun, Stopwatch timer)
    {
        timer.Stop();
        return new LaunchReport(
            false,
            dryRun,
            null,
            null,
            [new LaunchStepResult(stepId, LaunchStepStatus.Failed, message) { Duration = timer.Elapsed }],
            timer.Elapsed);
    }
}
