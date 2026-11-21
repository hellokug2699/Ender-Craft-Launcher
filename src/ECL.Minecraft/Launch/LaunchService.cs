// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public interface ILaunchService
{
    /// <summary>
    /// 当前注册的启动步骤 ID（按执行顺序）。主页按真实流程展示步骤，不预先硬编码步骤表。
    /// </summary>
    IReadOnlyList<string> Steps { get; }

    Task<LaunchReport> LaunchAsync(string instanceId, bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="progress"/> 按步骤开始/结束推送实时进度（home.md §3.2）；
    /// 事件只用于呈现，最终结果仍以 <see cref="LaunchReport"/> 为唯一权威。
    /// <paramref name="confirmSecurityRisk"/> 是 GUI 的风险确认通道：启动前安全检查发现
    /// 未接受的风险时，由它把风险披露给用户并取回“继续/取消”的决定；为 null 时只能依赖
    /// <paramref name="acceptSecurityRisk"/> 显式放行（CLI），不得静默继续。
    /// </summary>
    Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        bool acceptSecurityRisk = false,
        CancellationToken cancellationToken = default,
        IProgress<LaunchStepProgress>? progress = null,
        SecurityRiskConsentHandler? confirmSecurityRisk = null);
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
    private readonly IReadOnlyList<string> _stepIds;
    private readonly ILaunchStatusStore _status;
    private readonly Ecl.Sessions.IGameSessionService _sessions;
    private readonly ILocalizer _localizer;
    private readonly ILogger<LaunchService> _logger;

    public LaunchService(
        IInstanceService instances,
        IAccountService accounts,
        ISettingsService settings,
        IAppPaths paths,
        IEnumerable<ILaunchStep> steps,
        ILaunchStatusStore status,
        Ecl.Sessions.IGameSessionService sessions,
        ILocalizer localizer,
        ILogger<LaunchService> logger)
    {
        _instances = instances;
        _accounts = accounts;
        _settings = settings;
        _paths = paths;
        _steps = steps.OrderBy(step => step.Order).ToArray();
        _stepIds = _steps.Select(step => step.Id).ToArray();
        _status = status;
        _sessions = sessions;
        _localizer = localizer;
        _logger = logger;
    }

    public IReadOnlyList<string> Steps => _stepIds;

    public Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(instanceId, dryRun, acceptJavaRisk: false, acceptSecurityRisk: false, cancellationToken);

    public async Task<LaunchReport> LaunchAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        bool acceptSecurityRisk = false,
        CancellationToken cancellationToken = default,
        IProgress<LaunchStepProgress>? progress = null,
        SecurityRiskConsentHandler? confirmSecurityRisk = null)
    {
        // 真实启动才写入“最近一次启动状态”：Dry Run 只预览命令，不是一次启动。
        var record = !dryRun && AppPaths.IsValidInstanceId(instanceId);
        if (record)
        {
            _status.RecordStarted(instanceId);
        }

        var report = await LaunchCoreAsync(
            instanceId,
            dryRun,
            acceptJavaRisk,
            acceptSecurityRisk,
            cancellationToken,
            progress,
            confirmSecurityRisk).ConfigureAwait(false);

        if (record)
        {
            _status.RecordCompleted(instanceId, report);
        }

        return report;
    }

    private async Task<LaunchReport> LaunchCoreAsync(
        string instanceId,
        bool dryRun,
        bool acceptJavaRisk,
        bool acceptSecurityRisk,
        CancellationToken cancellationToken,
        IProgress<LaunchStepProgress>? progress,
        SecurityRiskConsentHandler? confirmSecurityRisk)
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
            AcceptSecurityRisk = acceptSecurityRisk,
            ConfirmSecurityRisk = dryRun ? null : confirmSecurityRisk,
        };

        // F-09 S1：真实启动在进入第一条步骤前就建立会话，使 validate/version/java 等
        // 前置阶段也能带上 sessionId 写入结构化事件；Dry Run 不是一次启动，不建会话。
        if (!dryRun)
        {
            EnsureSession(context);
        }

        var results = new List<LaunchStepResult>();
        var success = true;

        for (var index = 0; index < _steps.Count; index++)
        {
            var step = _steps[index];
            cancellationToken.ThrowIfCancellationRequested();
            var stepTimer = Stopwatch.StartNew();
            LaunchStepResult result;

            progress?.Report(new LaunchStepProgress
            {
                StepId = step.Id,
                Phase = LaunchStepProgressPhase.Started,
                Index = index,
                Total = _steps.Count,
            });
            RecordStage(context.SessionId, LaunchStageEvent.Started(step.Id, index, _steps.Count));

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

            progress?.Report(new LaunchStepProgress
            {
                StepId = result.StepId,
                Phase = LaunchStepProgressPhase.Completed,
                Index = index,
                Total = _steps.Count,
                Status = result.Status,
                Message = result.Message,
                Duration = result.Duration,
            });
            RecordStage(
                context.SessionId,
                LaunchStageEvent.Completed(
                    result.StepId,
                    index,
                    _steps.Count,
                    result.Status.ToString(),
                    result.Message,
                    (long)result.Duration.TotalMilliseconds));

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

        // 进程启动前失败：会话已经有阶段证据，标为中断而不是让它停在 Preparing。
        if (!success && context.ProcessId is null && context.SessionId is { } failedSession &&
            results.Count > 0 && !string.Equals(results[^1].StepId, "start", StringComparison.Ordinal))
        {
            try
            {
                _sessions.MarkState(
                    failedSession,
                    Ecl.Sessions.GameSessionState.Interrupted,
                    "启动在进程启动前失败");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(exception, "标记启动失败会话状态时出错：{SessionId}", failedSession);
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
            context.JavaPlan,
            context.SessionId);
        PersistJavaPlan(instance.Id, dryRun, success, context.JavaPlan);
        return report;
    }

    /// <summary>
    /// F-09 S1：进入流水线前建立会话，使所有阶段事件都带 sessionId。失败不阻塞启动，
    /// start 步骤会再次尝试创建（保持原有“进程启动前必须有会话”的约束）。
    /// </summary>
    private void EnsureSession(LaunchContext context)
    {
        try
        {
            var showConsole = _settings.Get<bool>("game.showConsole");
            var session = _sessions.Create(new Ecl.Sessions.SessionCreateRequest
            {
                InstanceId = context.Instance.Id,
                InstanceName = context.Instance.Name,
                GameDirectory = context.GameDirectory,
                MinecraftVersion = context.Instance.GameVersion,
                Loader = context.Instance.Loader,
                LogIsolation = showConsole ? "unavailable" : "pending",
            });
            context.SessionId = session.SessionId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "创建启动会话失败；启动继续，由 start 步骤再次尝试。");
        }
    }

    /// <summary>阶段事件写入失败只记警告：观察数据缺失不能改变启动结果。</summary>
    private void RecordStage(string? sessionId, LaunchStageEvent stage)
    {
        if (sessionId is null)
        {
            return;
        }

        try
        {
            _sessions.AppendLaunchStage(sessionId, stage);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "写入启动阶段事件失败：{StepId}/{Phase}", stage.StepId, stage.Phase);
        }
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
