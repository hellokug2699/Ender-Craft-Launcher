// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

public sealed record InstanceRelocationPlan(
    string PlanId,
    string PlanHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string InstanceId,
    string SourceDirectory,
    string TargetDirectory,
    InstanceOwnership Ownership,
    int SourceFiles,
    int TargetFiles,
    long SourceBytes,
    long TargetBytes,
    bool AllowMissingContent,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record InstanceRelocationResult(
    GameInstance Instance,
    string PlanHash,
    string TargetDirectory,
    IReadOnlyList<string> Evidence);

public interface IInstanceRelocationService
{
    Task<Result<InstanceRelocationPlan>> PlanAsync(
        string instanceId,
        string targetDirectory,
        bool allowMissingContent = false,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceRelocationResult>> ExecuteAsync(
        InstanceRelocationPlan plan,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-03 重定位：只重新关联已存在的同实例目录，不移动文件。
/// 计划含内容证据；登记与实例记录按 journal 对账，失败时尽量恢复旧关联。
/// </summary>
public sealed class InstanceRelocationService : IInstanceRelocationService
{
    private readonly IInstanceService _instances;
    private readonly IInstanceStore _store;
    private readonly InstanceIdentityService _identity;
    private readonly IInstanceOwnershipClassifier _ownership;
    private readonly IInstanceActivityGuard _guard;
    private readonly IInstancePathPolicy _pathPolicy;
    private readonly IInstanceWriteGate _gate;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceRelocationService> _logger;
    private readonly TimeProvider _time;

    public InstanceRelocationService(
        IInstanceService instances,
        IInstanceStore store,
        InstanceIdentityService identity,
        IInstanceOwnershipClassifier ownership,
        IInstanceActivityGuard guard,
        IInstancePathPolicy pathPolicy,
        IInstanceWriteGate gate,
        IAppPaths paths,
        ILogger<InstanceRelocationService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _store = store;
        _identity = identity;
        _ownership = ownership;
        _guard = guard;
        _pathPolicy = pathPolicy;
        _gate = gate;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceRelocationService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<InstanceRelocationPlan>> PlanAsync(
        string instanceId,
        string targetDirectory,
        bool allowMissingContent = false,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceRelocationPlan>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceRelocationPlan>.Fail("instance.not_found", "实例不存在。");
        }

        var blockers = new List<string>();
        var warnings = new List<string>();
        var evidence = new List<string>();
        var targetCheck = _pathPolicy.CheckTarget(targetDirectory, allowMissing: false);
        if (!targetCheck.IsAllowed)
        {
            return Result<InstanceRelocationPlan>.Fail(
                targetCheck.ErrorCode ?? "instance.path.invalid",
                targetCheck.Detail ?? "目标路径不合法。");
        }

        var target = targetCheck.FinalPath!;
        var source = InstancePaths.GetGameDirectory(_paths, instance);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)),
                Path.TrimEndingDirectorySeparator(target),
                StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("instance.lifecycle.already_at_target");
        }

        if (!Directory.Exists(target))
        {
            blockers.Add("instance.lifecycle.target_unavailable");
        }

        var ownership = await _ownership.ClassifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsSuccess)
        {
            blockers.Add(ownership.Error!.Code);
        }
        else if (ownership.Value!.Ownership == InstanceOwnership.Unknown)
        {
            blockers.Add("instance.lifecycle.unsupported_ownership");
        }
        else
        {
            evidence.AddRange(ownership.Value.Evidence);
        }

        var marker = await _identity.ReadMarkerAsync(target, cancellationToken).ConfigureAwait(false);
        if (marker.State == InstanceMarkerReadState.Valid)
        {
            if (!string.Equals(marker.Marker!.InstanceId, instanceId, StringComparison.Ordinal))
            {
                blockers.Add("identity.marker_conflict");
                warnings.Add($"目标目录标记属于 {marker.Marker.InstanceId}。");
            }
            else
            {
                evidence.Add("target_marker_matches_instance");
            }
        }
        else
        {
            var registrations = await _identity.ListAsync(cancellationToken).ConfigureAwait(false);
            var registration = registrations.IsSuccess
                ? registrations.Value!.FirstOrDefault(item =>
                    string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal))
                : null;
            if (registration is not null &&
                string.Equals(registration.MarkerAnchor, "instance", StringComparison.Ordinal))
            {
                blockers.Add("identity.marker_missing");
            }
            else
            {
                warnings.Add("目标目录没有可验证标记；按本地锚点重新关联。");
            }
        }

        var all = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var occupied = all
            .Where(other => !string.Equals(other.Id, instanceId, StringComparison.Ordinal))
            .FirstOrDefault(other => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstancePaths.GetGameDirectory(_paths, other))),
                target,
                StringComparison.OrdinalIgnoreCase));
        if (occupied is not null)
        {
            blockers.Add("identity.path_conflict");
            warnings.Add($"目标目录已登记为实例 {occupied.Id}。");
        }

        var activity = await _guard.InspectAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (activity.IsSuccess)
        {
            if (activity.Value!.HasActiveSession)
            {
                blockers.Add("instance.lifecycle.instance_running");
            }

            if (activity.Value.HasPendingTransaction)
            {
                blockers.Add("instance.lifecycle.operation_conflict");
            }

            if (activity.Value.IdentityConflict)
            {
                blockers.Add("instance.lifecycle.identity_conflict");
            }
        }

        var (sourceFiles, sourceBytes) = Measure(source);
        var (targetFiles, targetBytes) = Measure(target);
        evidence.Add($"source_files={sourceFiles};target_files={targetFiles}");
        if (!allowMissingContent)
        {
            foreach (var relative in new[] { "options.txt", "servers.dat" })
            {
                var sourceFile = Path.Combine(source, relative);
                var targetFile = Path.Combine(target, relative);
                if (File.Exists(sourceFile) && !File.Exists(targetFile))
                {
                    blockers.Add("instance.lifecycle.verification_failed");
                    warnings.Add($"目标缺少关键文件：{relative}");
                }
            }

            if (sourceFiles > 0 && targetFiles == 0)
            {
                blockers.Add("instance.lifecycle.verification_failed");
                warnings.Add("目标目录为空，无法确认是同实例内容。");
            }
        }

        var issued = _time.GetUtcNow();
        var planId = $"rel-{issued:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
        var keyHash = ComputeKeyHash(target);
        var hash = ComputeHash(instanceId, source, target, sourceFiles, sourceBytes, targetFiles, targetBytes, keyHash);
        return Result<InstanceRelocationPlan>.Ok(new InstanceRelocationPlan(
            planId,
            hash,
            issued,
            issued.AddMinutes(10),
            instanceId,
            source,
            target,
            ownership.IsSuccess ? ownership.Value!.Ownership : InstanceOwnership.Unknown,
            sourceFiles,
            targetFiles,
            sourceBytes,
            targetBytes,
            allowMissingContent,
            evidence,
            blockers,
            warnings));
    }

    public async Task<Result<InstanceRelocationResult>> ExecuteAsync(
        InstanceRelocationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_time.GetUtcNow() > plan.ExpiresAt)
        {
            return Result<InstanceRelocationResult>.Fail(
                "instance.lifecycle.plan_stale",
                "重定位计划已过期，请重新检查。");
        }

        var current = await PlanAsync(
            plan.InstanceId,
            plan.TargetDirectory,
            plan.AllowMissingContent,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result<InstanceRelocationResult>.Fail(current.Error!);
        }

        if (!string.Equals(current.Value!.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstanceRelocationResult>.Fail(
                "instance.lifecycle.plan_stale",
                "源/目标内容在计划后发生变化，请重新检查。");
        }

        if (plan.Blockers.Count > 0)
        {
            return Result<InstanceRelocationResult>.Fail(
                "instance.lifecycle.blocked",
                "重定位计划存在阻断项。",
                string.Join("; ", plan.Blockers));
        }

        var leaseResult = await _gate.AcquireAsync(plan.InstanceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceRelocationResult>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var instance = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceRelocationResult>.Fail("instance.not_found", "实例不存在。");
        }

        var journalRoot = Path.Combine(
            _paths.ConfigDirectory,
            "instance-engine",
            "lifecycle",
            plan.InstanceId);
        Directory.CreateDirectory(journalRoot);
        var journalPath = Path.Combine(journalRoot, $"{plan.PlanId}.json");

        var relocated = await _identity.RelocateAsync(plan.InstanceId, plan.TargetDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!relocated.IsSuccess)
        {
            return Result<InstanceRelocationResult>.Fail(relocated.Error!);
        }

        var updated = instance with
        {
            GameDirectory = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.TargetDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.GetInstanceGameDirectory(instance.Id))),
                StringComparison.OrdinalIgnoreCase)
                ? null
                : plan.TargetDirectory,
        };

        try
        {
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            var verification = await _identity.VerifyAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
            if (!verification.IsSuccess || verification.Value!.Status == InstanceRegistrationStatus.Conflict)
            {
                throw new InvalidOperationException("重定位后的身份校验失败。");
            }

            await File.WriteAllTextAsync(
                journalPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        planId = plan.PlanId,
                        planHash = plan.PlanHash,
                        instanceId = plan.InstanceId,
                        source = plan.SourceDirectory,
                        target = plan.TargetDirectory,
                        state = "Committed",
                        committedAt = _time.GetUtcNow(),
                    },
                    EclJson.Indented),
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("实例 {InstanceId} 已重定位到 {Target}。", plan.InstanceId, plan.TargetDirectory);
            return Result<InstanceRelocationResult>.Ok(new InstanceRelocationResult(
                updated,
                plan.PlanHash,
                plan.TargetDirectory,
                plan.Evidence));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            // 记录写入失败：尝试把登记恢复到旧路径，避免两边不一致。
            var reverted = await _identity.RelocateAsync(plan.InstanceId, instance.GameDirectory ?? plan.SourceDirectory, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                journalPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        planId = plan.PlanId,
                        planHash = plan.PlanHash,
                        instanceId = plan.InstanceId,
                        source = plan.SourceDirectory,
                        target = plan.TargetDirectory,
                        state = reverted.IsSuccess ? "Failed" : "NeedsRecovery",
                        error = exception.Message,
                        updatedAt = _time.GetUtcNow(),
                    },
                    EclJson.Indented),
                CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceRelocationResult>.Fail(
                reverted.IsSuccess ? "instance.lifecycle.verification_failed" : "instance.lifecycle.needs_recovery",
                "重定位失败；登记与记录的最终状态请检查生命周期 journal。",
                exception.Message);
        }
    }

    private static (int Files, long Bytes) Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                files++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 不可读文件不计数。
            }
        }

        return (files, bytes);
    }

    private static string ComputeKeyHash(string target)
    {
        var builder = new StringBuilder();
        foreach (var relative in new[] { ".ecl/instance.json", "options.txt", "servers.dat" })
        {
            var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                builder.Append(relative).Append(":missing|");
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                builder.Append(relative).Append(':')
                    .Append(Convert.ToHexStringLower(SHA256.HashData(stream)))
                    .Append('|');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                builder.Append(relative).Append(":unreadable|");
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ComputeHash(
        string instanceId,
        string source,
        string target,
        int sourceFiles,
        long sourceBytes,
        int targetFiles,
        long targetBytes,
        string keyHash)
    {
        var text = string.Join(
            '|',
            instanceId,
            source,
            target,
            sourceFiles,
            sourceBytes,
            targetFiles,
            targetBytes,
            keyHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
