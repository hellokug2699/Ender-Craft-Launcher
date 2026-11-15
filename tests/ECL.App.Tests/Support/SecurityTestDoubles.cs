// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Additions;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Minecraft.Security;

namespace Ecl.App.Tests.Support;

/// <summary>安全中心测试替身：按实例返回预设扫描报告，并记录接受/隔离调用。</summary>
internal sealed class FakeSecurityService : IThreatProtectionService
{
    public Func<string, CancellationToken, Task<Result<ThreatScanReport>>> Scan { get; set; } =
        (instanceId, _) => Task.FromResult(Result<ThreatScanReport>.Ok(Report(instanceId)));

    public List<string> ScannedInstances { get; } = [];

    public List<(string InstanceId, IReadOnlyList<ThreatFinding> Findings)> Accepted { get; } = [];

    public List<(string InstanceId, string RelativePath)> Quarantined { get; } = [];

    public Func<string, ThreatFinding[]>? QuarantineFindings { get; set; }

    public Result QuarantineResult { get; set; } = Result.Ok();

    public Result AcceptResult { get; set; } = Result.Ok();

    public Task<Result<ThreatScanReport>> ScanAsync(
        string instanceId,
        SecurityScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ScannedInstances.Add(instanceId);
        return Scan(instanceId, cancellationToken);
    }

    public Task<Result<ThreatScanReport?>> GetLatestScanAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("安全中心不得读取历史扫描记录。");

    public Task<Result<LaunchSecurityDecision>> EvaluateLaunchAsync(
        string instanceId,
        IReadOnlyList<string>? launchArguments = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> AcceptRiskAsync(
        string instanceId,
        IReadOnlyList<ThreatFinding> findings,
        CancellationToken cancellationToken = default)
    {
        Accepted.Add((instanceId, findings));
        return Task.FromResult(AcceptResult);
    }

    public Task<Result<QuarantineRecord>> QuarantineAsync(
        string instanceId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        Quarantined.Add((instanceId, relativePath));
        if (!QuarantineResult.IsSuccess)
        {
            return Task.FromResult(Result<QuarantineRecord>.Fail(QuarantineResult.Error!));
        }

        return Task.FromResult(Result<QuarantineRecord>.Ok(new QuarantineRecord(
            "q-1",
            instanceId,
            relativePath,
            "hash",
            1,
            DateTimeOffset.UtcNow,
            ThreatLevel.Suspicious)));
    }

    public Task<Result> RestoreQuarantineAsync(
        string instanceId,
        string quarantineId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result<IReadOnlyList<QuarantineRecord>>> ListQuarantineAsync(
        string instanceId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public static ThreatScanReport Report(
        string instanceId,
        params ThreatFinding[] findings) => new(
        $"scan-{instanceId}",
        instanceId,
        DateTimeOffset.Now,
        "1.0.0",
        "2026-11-03",
        findings,
        10,
        2,
        true,
        []);

    public static ThreatFinding Finding(
        string relativePath,
        ThreatLevel level,
        string? sha256 = "hash",
        string ruleId = "file.executable") => new(
        $"{relativePath}-1",
        relativePath,
        sha256,
        level,
        [new ThreatEvidence(ruleId, "2026-11-03", level, "evidence")],
        "recommendation");
}

/// <summary>状态中心数据源替身：按队列返回报告并记录调用次数。</summary>
internal sealed class FakeRuntimeStatusService : IRuntimeStatusService
{
    public Queue<RuntimeStatusReport> Reports { get; } = new();

    public int Calls { get; private set; }

    public Task<RuntimeStatusReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Reports.Count == 0)
        {
            return Task.FromResult(new RuntimeStatusReport(
                true,
                null,
                "0.8.0",
                DateTimeOffset.Now,
                []));
        }

        return Task.FromResult(Reports.Dequeue());
    }
}
