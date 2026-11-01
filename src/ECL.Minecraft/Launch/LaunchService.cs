// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public interface ILaunchService
{
    Task<LaunchReport> LaunchAsync(string instanceId, bool dryRun, CancellationToken cancellationToken = default);

    Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        CancellationToken cancellationToken = default);
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

    public Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(instanceId, dryRun, acceptJavaRisk: false, cancellationToken);

    public async Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        CancellationToken cancellationToken = default)
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
            var refreshed = await _accounts
                .EnsureFreshAsync(
                    account,
                    new MicrosoftAuthenticationOptions { ClientId = MicrosoftClientIdentity.DefaultClientId },
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

        else if (!dryRun && account.Type == AccountType.ThirdParty)
        {
            // 第三方账户没有固定的过期时间：启动前向认证服务器校验令牌，
            // 被拒绝时才静默 refresh，绝不会在这里弹出浏览器或要求输入密码。
            var refreshed = await _accounts
                .EnsureThirdPartyFreshAsync(account, cancellationToken)
                .ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return Failure("account", refreshed.Error?.Message ?? "第三方账户凭据不可用。", dryRun, total);
            }

            account = refreshed.Value!;
        }

        if (!dryRun && account.Type is AccountType.Microsoft or AccountType.ThirdParty &&
            string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return Failure("account", "账户登录凭据不可用，请重新登录。", dryRun, total);
        }

        var context = new LaunchContext
        {
            Instance = instance,
            Account = account,
            Paths = _paths,
            Settings = _settings,
            DryRun = dryRun,
            AcceptJavaRisk = acceptJavaRisk,
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
                _logger.LogWarning(
                    "启动步骤 {StepId} 失败：{Message}",
                    result.StepId,
                    result.Message);
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

        var report = new LaunchReport(
            success,
            dryRun,
            context.Command?.Display,
            context.ProcessId,
            results,
            total.Elapsed,
            context.JavaPlan);
        PersistJavaPlan(instance.Id, dryRun, success, context.JavaPlan);
        return report;
    }

    /// <summary>
    /// 启动计划需要可复现：记录最终 Java 路径、版本、架构、要求摘要、选择原因与被拒候选。
    /// 写入失败不能影响启动，只记录日志。
    /// </summary>
    private void PersistJavaPlan(
        string instanceId,
        bool dryRun,
        bool success,
        JavaLaunchPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(_paths.LogsDirectory, "launch");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"launch-{instanceId}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{(dryRun ? "dryrun" : "launch")}.json");
            var payload = new
            {
                recordedAt = DateTimeOffset.UtcNow.ToString("O"),
                instanceId,
                dryRun,
                success,
                java = plan,
            };
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(payload, Ecl.Core.Json.EclJson.Indented));
            _logger.LogInformation(
                "已记录启动 Java 计划：{Path}（{Reason}，{Requirement}）。",
                path,
                plan.SelectionReason,
                plan.RequirementSummary ?? "unknown");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "写入启动 Java 计划失败，不影响本次启动。");
        }
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
