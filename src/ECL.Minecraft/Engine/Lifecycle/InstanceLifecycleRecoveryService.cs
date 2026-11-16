// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

public sealed record InstanceLifecycleRecoveryEntry(
    InstanceLifecycleRecord Record,
    string Action,
    IReadOnlyList<string> Notes);

/// <summary>没有任何 journal 对应的生命周期残留（隔离区/退休区/staging/已分离数据）。</summary>
public sealed record InstanceLifecycleOrphan(
    string Kind,
    string Path,
    string? InstanceId,
    string Detail);

public sealed record InstanceLifecycleRecoveryReport(
    DateTimeOffset ScannedAt,
    IReadOnlyList<InstanceLifecycleRecoveryEntry> Entries,
    IReadOnlyList<InstanceLifecycleOrphan> Orphans)
{
    public int NeedsRecoveryCount => Entries.Count(entry => entry.Record.NeedsRecovery);

    public int ResumableCount => Entries.Count(entry => entry.Action is "resume" or "resume_purge");

    public int CleanableSourceCount => Entries.Count(entry => entry.Action == "cleanup_source");

    public int OrphanCount => Orphans.Count;
}

public sealed record InstanceLifecycleSourceCleanupResult(
    string PlanId,
    string InstanceId,
    string? DeletedPath,
    IReadOnlyList<string> Notes);

public interface IInstanceLifecycleRecoveryService
{
    /// <summary>
    /// 扫描生命周期 journal、隔离区、退休区与迁移 staging，只报告不自动执行（F-03 §3.7/§5.8）。
    /// </summary>
    Task<Result<InstanceLifecycleRecoveryReport>> InspectAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>继续中断的可恢复操作；破坏性步骤仍按 journal 阶段执行。</summary>
    Task<Result<InstanceLifecycleRecord>> ResumeAsync(
        string planId,
        CancellationToken cancellationToken = default);

    /// <summary>丢弃未切换的 staging 或废弃未提交的 journal；已切换的不允许丢弃。</summary>
    Task<Result<InstanceLifecycleRecord>> DiscardAsync(
        string planId,
        CancellationToken cancellationToken = default);

    /// <summary>清理“.ecl-retired”中已提交移动的退休源；必须先复验目标提交与路径约束。</summary>
    Task<Result<InstanceLifecycleSourceCleanupResult>> CleanupSourceAsync(
        string planId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-03 生命周期恢复入口：把散落的 move/delete/reconfigure journal 收敛到一个可枚举、
/// 可继续、可放弃、可清理退休源的入口，供 CLI、诊断与（未来）启动自检使用。
/// 不自动前向完成“无法证明一致”的状态，也不删除隔离区/退休区以外的路径。
/// </summary>
public sealed class InstanceLifecycleRecoveryService : IInstanceLifecycleRecoveryService
{
    private readonly InstanceLifecycleJournalStore _journals;
    private readonly IInstanceMoveService _moves;
    private readonly IInstanceDeletionService _deletions;
    private readonly IWorkspaceService _workspace;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceLifecycleRecoveryService> _logger;
    private readonly TimeProvider _time;

    public InstanceLifecycleRecoveryService(
        InstanceLifecycleJournalStore journals,
        IInstanceMoveService moves,
        IInstanceDeletionService deletions,
        IWorkspaceService workspace,
        IAppPaths paths,
        ILogger<InstanceLifecycleRecoveryService>? logger = null,
        TimeProvider? time = null)
    {
        _journals = journals;
        _moves = moves;
        _deletions = deletions;
        _workspace = workspace;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceLifecycleRecoveryService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<InstanceLifecycleRecoveryReport>> InspectAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var records = await _journals.ListAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var entries = records
            .Select(record => new InstanceLifecycleRecoveryEntry(
                record,
                record.SuggestedAction,
                BuildNotes(record)))
            .ToArray();

        var known = records
            .SelectMany(record => new[] { record.TombstonePath, record.RetiredSourcePath, record.StagingPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathIdentity.Normalize(path!))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = new List<InstanceLifecycleOrphan>();
        foreach (var root in EnumerateRoots())
        {
            CollectOrphans(root, "quarantine", ".ecl-trash", known, orphans);
            CollectOrphans(root, "retired_source", ".ecl-retired", known, orphans);
            CollectStaging(root, known, orphans);
            CollectDetachedData(root, orphans);
        }

        return Result<InstanceLifecycleRecoveryReport>.Ok(new InstanceLifecycleRecoveryReport(
            _time.GetUtcNow(),
            entries,
            orphans));
    }

    public async Task<Result<InstanceLifecycleRecord>> ResumeAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var recordResult = await _journals.ReadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!recordResult.IsSuccess)
        {
            return recordResult;
        }

        var record = recordResult.Value!;
        switch (record.Kind)
        {
            case InstanceLifecycleKind.Move:
            {
                var resumed = await _moves.ResumeAsync(planId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!resumed.IsSuccess)
                {
                    return Result<InstanceLifecycleRecord>.Fail(resumed.Error!);
                }

                return await ReReadAsync(planId, cancellationToken).ConfigureAwait(false);
            }

            case InstanceLifecycleKind.Delete:
            {
                var resumed = await _deletions.ResumeCleanupAsync(planId, cancellationToken).ConfigureAwait(false);
                if (!resumed.IsSuccess)
                {
                    return Result<InstanceLifecycleRecord>.Fail(resumed.Error!);
                }

                return await ReReadAsync(planId, cancellationToken).ConfigureAwait(false);
            }

            case InstanceLifecycleKind.Reconfigure:
                return record.IsTerminal
                    ? Result<InstanceLifecycleRecord>.Ok(record)
                    : Result<InstanceLifecycleRecord>.Fail(
                        "instance.lifecycle.resume_invalid",
                        "重配置 journal 不自动重放；请检查变更前快照与实例记录后重新生成计划。",
                        record.State);

            default:
                return Result<InstanceLifecycleRecord>.Fail(
                    "instance.lifecycle.resume_invalid",
                    "该类型生命周期操作没有自动恢复入口。",
                    record.Kind.ToString());
        }
    }

    public async Task<Result<InstanceLifecycleRecord>> DiscardAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var recordResult = await _journals.ReadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!recordResult.IsSuccess)
        {
            return recordResult;
        }

        var record = recordResult.Value!;
        switch (record.Kind)
        {
            case InstanceLifecycleKind.Move:
            {
                var discarded = await _moves.DiscardAsync(planId, cancellationToken).ConfigureAwait(false);
                if (!discarded.IsSuccess)
                {
                    return Result<InstanceLifecycleRecord>.Fail(discarded.Error!);
                }

                return await ReReadAsync(planId, cancellationToken).ConfigureAwait(false);
            }

            case InstanceLifecycleKind.Delete:
            {
                if (!string.IsNullOrWhiteSpace(record.TombstonePath) && Directory.Exists(record.TombstonePath))
                {
                    return Result<InstanceLifecycleRecord>.Fail(
                        "instance.lifecycle.discard_invalid",
                        "隔离区已存在，只能继续清理，不能丢弃删除记录。");
                }

                var journal = await _deletions.GetAsync(planId, cancellationToken).ConfigureAwait(false);
                if (!journal.IsSuccess)
                {
                    return Result<InstanceLifecycleRecord>.Fail(journal.Error!);
                }

                if (journal.Value!.State == InstanceDeletionState.Purged)
                {
                    return await ReReadAsync(planId, cancellationToken).ConfigureAwait(false);
                }

                var abandoned = journal.Value! with
                {
                    State = InstanceDeletionState.Purged,
                    UpdatedAt = _time.GetUtcNow(),
                    ErrorCode = null,
                    ErrorMessage = "已放弃清理：隔离区不存在或已为空。",
                };
                var written = await _journals.WriteAsync(
                    abandoned.InstanceId,
                    abandoned.PlanId,
                    abandoned,
                    cancellationToken).ConfigureAwait(false);
                if (!written.IsSuccess)
                {
                    return Result<InstanceLifecycleRecord>.Fail(written.Error!);
                }

                return await ReReadAsync(planId, cancellationToken).ConfigureAwait(false);
            }

            case InstanceLifecycleKind.Reconfigure:
                if (record.State is "RolledBack" or "Completed")
                {
                    return Result<InstanceLifecycleRecord>.Ok(record);
                }

                return Result<InstanceLifecycleRecord>.Fail(
                    "instance.lifecycle.discard_invalid",
                    "重配置包含元数据提交点，不存在可安全丢弃的中间态；请检查后重新计划。",
                    record.State);

            default:
                return Result<InstanceLifecycleRecord>.Fail(
                    "instance.lifecycle.discard_invalid",
                    "该类型生命周期操作没有丢弃入口。",
                    record.Kind.ToString());
        }
    }

    public async Task<Result<InstanceLifecycleSourceCleanupResult>> CleanupSourceAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var recordResult = await _journals.ReadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!recordResult.IsSuccess)
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(recordResult.Error!);
        }

        var record = recordResult.Value!;
        if (record.Kind != InstanceLifecycleKind.Move)
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(
                "instance.lifecycle.source_cleanup_invalid",
                "只有已提交的移动计划存在退休源。");
        }

        if (record.State is not ("Committed" or "Completed" or "SourceCleaned"))
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(
                "instance.lifecycle.source_cleanup_invalid",
                $"journal 状态 {record.State} 不允许清理退休源。");
        }

        if (string.IsNullOrWhiteSpace(record.RetiredSourcePath))
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Ok(
                new InstanceLifecycleSourceCleanupResult(
                    planId,
                    record.InstanceId,
                    null,
                    ["退休源已清理或从未存在。"]));
        }

        var normalized = PathIdentity.Normalize(record.RetiredSourcePath);
        if (normalized is null)
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(
                "instance.path.invalid",
                "退休源路径无法规范化。");
        }

        var validation = ValidateRetiredPath(record.InstanceId, normalized);
        if (!validation.IsSuccess)
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(validation.Error!);
        }

        var notes = new List<string>();
        if (Directory.Exists(normalized))
        {
            var failures = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    {
                        failures.Add($"跳过重解析点：{Path.GetFileName(entry)}");
                        continue;
                    }

                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else if (File.Exists(entry))
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(entry)}：{exception.Message}");
                }
            }

            try
            {
                if (Directory.Exists(normalized) && !Directory.EnumerateFileSystemEntries(normalized).Any())
                {
                    Directory.Delete(normalized);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"退休源目录：{exception.Message}");
            }

            if (failures.Count > 0)
            {
                notes.AddRange(failures);
                _logger.LogWarning(
                    "退休源 {Path} 未完全清理，保留 journal 供重试。",
                    normalized);
                return Result<InstanceLifecycleSourceCleanupResult>.Fail(
                    "instance.lifecycle.needs_recovery",
                    "退休源未完全清理；剩余项已保留，可重试。",
                    string.Join("; ", failures.Take(5)));
            }

            notes.Add($"已删除退休源：{normalized}");
        }
        else
        {
            notes.Add("退休源目录已不存在；仅更新 journal。");
        }

        var marked = await _moves.MarkSourceCleanedAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!marked.IsSuccess)
        {
            return Result<InstanceLifecycleSourceCleanupResult>.Fail(marked.Error!);
        }

        return Result<InstanceLifecycleSourceCleanupResult>.Ok(
            new InstanceLifecycleSourceCleanupResult(planId, record.InstanceId, normalized, notes));
    }

    private async Task<Result<InstanceLifecycleRecord>> ReReadAsync(
        string planId,
        CancellationToken cancellationToken) =>
        await _journals.ReadAsync(planId, cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<string> BuildNotes(InstanceLifecycleRecord record)
    {
        var notes = new List<string> { $"state={record.State}" };
        if (record.NeedsRecovery)
        {
            notes.Add("需要人工检查：不能用自动恢复覆盖无法证明的状态。");
        }

        if (!string.IsNullOrWhiteSpace(record.ErrorCode))
        {
            notes.Add($"error={record.ErrorCode}");
        }

        if (!string.IsNullOrWhiteSpace(record.TombstonePath))
        {
            notes.Add($"quarantine={record.TombstonePath}");
        }

        if (!string.IsNullOrWhiteSpace(record.RetiredSourcePath))
        {
            notes.Add($"retired={record.RetiredSourcePath}");
        }

        return notes;
    }

    private IEnumerable<string> EnumerateRoots()
    {
        var roots = new List<string> { _paths.GameRoot };
        if (_workspace.Primary is { } primary)
        {
            roots.Add(primary.Path);
        }

        roots.AddRange(_workspace.Roots
            .Where(root => root.Kind == WorkspaceKind.Managed)
            .Select(root => root.Path));
        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathIdentity.Normalize(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void CollectOrphans(
        string root,
        string kind,
        string directoryName,
        HashSet<string> known,
        List<InstanceLifecycleOrphan> orphans)
    {
        var directory = Path.Combine(root, directoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateDirectories(directory))
            {
                var normalized = PathIdentity.Normalize(entry);
                if (normalized is null || known.Contains(normalized))
                {
                    continue;
                }

                var instanceId = Path.GetFileName(normalized).Split('-').FirstOrDefault();
                orphans.Add(new InstanceLifecycleOrphan(
                    kind,
                    normalized,
                    string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
                    kind == "quarantine"
                        ? "没有对应 journal 的隔离区目录；可能是 journal 丢失或旧版本删除。"
                        : "没有对应 journal 的退休源目录；可能是移动 journal 丢失或手动移动。"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法枚举生命周期残留目录：{Path}", directory);
        }
    }

    private void CollectStaging(string root, HashSet<string> known, List<InstanceLifecycleOrphan> orphans)
    {
        var instances = Path.Combine(root, "instances");
        if (!Directory.Exists(instances))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateDirectories(instances, ".ecl-migrate-*"))
            {
                var normalized = PathIdentity.Normalize(entry);
                if (normalized is null || known.Contains(normalized))
                {
                    continue;
                }

                orphans.Add(new InstanceLifecycleOrphan(
                    "staging",
                    normalized,
                    null,
                    "没有对应 journal 的迁移 staging；可能可安全删除，但必须先确认没有实例依赖。"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法枚举迁移 staging：{Path}", instances);
        }
    }

    private void CollectDetachedData(string root, List<InstanceLifecycleOrphan> orphans)
    {
        var instances = Path.Combine(root, "instances");
        if (!Directory.Exists(instances))
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateDirectories(instances))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith(".ecl-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(entry, "instance.json")))
                {
                    continue;
                }

                if (!Directory.EnumerateFileSystemEntries(entry).Any())
                {
                    continue;
                }

                orphans.Add(new InstanceLifecycleOrphan(
                    "detached_game_data",
                    entry,
                    name,
                    "没有实例记录的目录：可能是 RemoveRecordOnly 留下的已分离游戏数据；不自动删除，也不当作损坏实例清理。"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法枚举已分离数据：{Path}", instances);
        }
    }

    private Result ValidateRetiredPath(string instanceId, string normalized)
    {
        var parent = Path.GetDirectoryName(normalized);
        if (parent is null ||
            !string.Equals(Path.GetFileName(parent), ".ecl-retired", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail(
                "instance.path.owner_scope",
                "退休源路径不在 .ecl-retired 下，拒绝清理。");
        }

        var name = Path.GetFileName(normalized);
        if (!name.StartsWith($"{instanceId}-", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail(
                "instance.path.owner_scope",
                "退休源目录名不属于该实例，拒绝清理。");
        }

        var root = Path.GetDirectoryName(parent);
        var allowed = EnumerateRoots().Any(rootPath =>
            PathIdentity.PathsEqual(rootPath, root ?? string.Empty));
        if (!allowed)
        {
            return Result.Fail(
                "instance.path.owner_scope",
                "退休源不在已登记的受管理根内，拒绝清理。");
        }

        return Result.Ok();
    }
}
