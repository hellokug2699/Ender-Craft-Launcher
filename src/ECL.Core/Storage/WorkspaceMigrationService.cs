// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Core.Storage;

/// <summary>
/// 工作区迁移：复制到目标 staging → 校验 → 原子切换配置 → 保留源目录。
/// 任何失败都不会删除或修改源工作区；目标切换失败时旧工作区继续有效。
/// </summary>
public sealed class WorkspaceMigrationService : IWorkspaceMigrationService
{
    private const string StagingSuffix = ".ecl-staging";
    private const long SpaceSafetyMargin = 256L * 1024 * 1024;
    private const long HashLimitBytes = 2L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<WorkspaceMigrationService> _logger;
    private readonly string _recordPath;

    public WorkspaceMigrationService(
        AppPaths paths,
        IWorkspaceService workspace,
        ILogger<WorkspaceMigrationService> logger)
    {
        _paths = paths;
        _workspace = workspace;
        _logger = logger;
        _recordPath = Path.Combine(paths.ConfigDirectory, "workspace-migration.json");
        Current = LoadRecord();
    }

    public WorkspaceMigrationRecord? Current { get; private set; }

    public Task<Result<WorkspaceMigrationPlan>> PlanAsync(
        string targetPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Plan(targetPath), cancellationToken);

    public Task<Result<WorkspaceMigrationRecord>> StartAsync(
        string targetPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(targetPath, resume: false), cancellationToken);

    public Task<Result<WorkspaceMigrationRecord>> ResumeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(null, resume: true), cancellationToken);

    public Task<Result> DiscardAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (Current is null)
                {
                    return Result.Fail("workspace.migration.none", "没有可放弃的迁移记录。");
                }

                TryDeleteStaging(Current.StagingPath, Current.SourcePath, Current.TargetPath);
                Current = Current with
                {
                    State = WorkspaceMigrationState.Discarded,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                SaveRecord(Current);
                return Result.Ok();
            },
            cancellationToken);

    private Result<WorkspaceMigrationPlan> Plan(string targetPath)
    {
        var primary = _workspace.Primary;
        if (primary is null || !_workspace.IsConfigured)
        {
            return Result<WorkspaceMigrationPlan>.Fail(
                "workspace.migration.not_configured",
                "尚未配置可迁移的工作区。");
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        var source = WorkspacePathValidator.NormalizePath(primary.Path);
        if (source is null || !Directory.Exists(source))
        {
            problems.Add("源工作区当前不可用，无法迁移。");
        }

        if (primary.Kind != WorkspaceKind.Managed)
        {
            problems.Add("外部工作区只登记引用，不支持由 ECL 迁移。");
        }

        var check = _workspace.Check(targetPath, createIfMissing: false);
        var target = check.Path;
        if (string.IsNullOrWhiteSpace(target))
        {
            problems.Add("目标路径无效。");
        }
        else
        {
            if (source is not null &&
                (WorkspacePathValidator.IsUnder(target, source) || WorkspacePathValidator.IsUnder(source, target)))
            {
                problems.Add("源与目标路径重叠，迁移会破坏数据。");
            }

            if (Directory.Exists(target))
            {
                var isEmpty = !Directory.EnumerateFileSystemEntries(target).Any();
                if (!isEmpty)
                {
                    problems.Add("目标目录已存在且不为空；ECL 不会合并或覆盖现有目录。");
                }
            }
            else if (check.Status != WorkspaceStatus.Missing)
            {
                problems.Add(check.ProblemMessage ?? "目标路径不可用。");
            }

            warnings.AddRange(check.WarningCodes);
        }

        var (totalBytes, totalFiles, instanceCount) = source is null
            ? (0L, 0, 0)
            : Measure(source, problems);
        if (source is not null && HasLockedSessionFile(source))
        {
            problems.Add("检测到被占用的世界会话文件，游戏可能仍在运行。请先退出游戏。");
        }

        var freeBytes = check.FreeBytes;
        if (freeBytes is { } free && totalBytes > 0 && free < totalBytes + SpaceSafetyMargin)
        {
            problems.Add($"目标空间不足：需要约 {FormatBytes(totalBytes + SpaceSafetyMargin)}，可用 {FormatBytes(free)}。");
        }

        var plan = new WorkspaceMigrationPlan(
            source ?? primary.Path,
            target,
            totalBytes,
            totalFiles,
            instanceCount,
            freeBytes,
            problems.Count == 0,
            problems,
            warnings);

        return Result<WorkspaceMigrationPlan>.Ok(plan);
    }

    private Result<WorkspaceMigrationRecord> Run(string? targetPath, bool resume)
    {
        WorkspaceMigrationRecord record;
        WorkspaceMigrationPlan plan;

        if (resume)
        {
            if (Current is null)
            {
                return Result<WorkspaceMigrationRecord>.Fail(
                    "workspace.migration.none",
                    "没有可恢复的迁移记录。");
            }

            if (Current.State == WorkspaceMigrationState.Completed)
            {
                return Result<WorkspaceMigrationRecord>.Ok(Current);
            }

            if (Current.State is WorkspaceMigrationState.Discarded or WorkspaceMigrationState.Failed)
            {
                return Result<WorkspaceMigrationRecord>.Fail(
                    "workspace.migration.unresumable",
                    "该迁移记录已结束，不能继续。");
            }

            var replanned = Plan(Current.TargetPath);
            if (!replanned.IsSuccess)
            {
                return Result<WorkspaceMigrationRecord>.Fail(replanned.Error!);
            }

            plan = replanned.Value!;
            if (!plan.CanProceed && plan.Problems.Any(problem => !problem.Contains("目标目录已存在", StringComparison.Ordinal)))
            {
                return Result<WorkspaceMigrationRecord>.Fail(
                    "workspace.migration.blocked",
                    plan.Problems.First());
            }

            record = Current;
        }
        else
        {
            var planned = Plan(targetPath ?? string.Empty);
            if (!planned.IsSuccess)
            {
                return Result<WorkspaceMigrationRecord>.Fail(planned.Error!);
            }

            plan = planned.Value!;
            if (!plan.CanProceed)
            {
                return Result<WorkspaceMigrationRecord>.Fail(
                    "workspace.migration.blocked",
                    plan.Problems.FirstOrDefault() ?? "迁移条件不满足。");
            }

            record = new WorkspaceMigrationRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                SourcePath = plan.SourcePath,
                TargetPath = plan.TargetPath,
                StagingPath = plan.TargetPath + StagingSuffix,
                State = WorkspaceMigrationState.ReadyToCopy,
                TotalBytes = plan.TotalBytes,
                TotalFiles = plan.TotalFiles,
            };
        }

        try
        {
            return Execute(record, plan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "工作区迁移失败：{Source} → {Target}", record.SourcePath, record.TargetPath);
            Update(record with
            {
                State = WorkspaceMigrationState.Interrupted,
                ErrorCode = "workspace.migration.io",
                ErrorMessage = exception.Message,
            });
            return Result<WorkspaceMigrationRecord>.Fail(
                "workspace.migration.io",
                $"迁移中断：{exception.Message}",
                "源工作区未被修改；可运行 workspace migrate --resume 或 --discard。");
        }
    }

    private Result<WorkspaceMigrationRecord> Execute(WorkspaceMigrationRecord record, WorkspaceMigrationPlan plan)
    {
        var staging = record.StagingPath;

        // 恢复时重新复制可避免半写文件；源目录永远只读。
        TryDeleteStaging(staging, record.SourcePath, record.TargetPath);
        Directory.CreateDirectory(staging);

        record = Update(record with
        {
            State = WorkspaceMigrationState.Copying,
            TotalBytes = plan.TotalBytes,
            TotalFiles = plan.TotalFiles,
        });

        var copied = 0;
        long copiedBytes = 0;
        foreach (var file in EnumerateFiles(record.SourcePath))
        {
            var relative = Path.GetRelativePath(record.SourcePath, file);
            var destination = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
            copiedBytes += new FileInfo(file).Length;

            if (copied % 200 == 0)
            {
                record = Update(record with
                {
                    CopiedFiles = copied,
                    TotalBytes = Math.Max(record.TotalBytes, copiedBytes),
                });
            }
        }

        record = Update(record with
        {
            State = WorkspaceMigrationState.Verifying,
            CopiedFiles = copied,
        });

        var verification = Verify(record.SourcePath, staging);
        if (!verification.IsSuccess)
        {
            Update(record with
            {
                State = WorkspaceMigrationState.Failed,
                ErrorCode = verification.Error!.Code,
                ErrorMessage = verification.Error.Message,
            });
            return Result<WorkspaceMigrationRecord>.Fail(verification.Error!);
        }

        record = Update(record with { State = WorkspaceMigrationState.Switching });

        if (Directory.Exists(record.TargetPath) &&
            !Directory.EnumerateFileSystemEntries(record.TargetPath).Any())
        {
            Directory.Delete(record.TargetPath);
        }

        Directory.Move(staging, record.TargetPath);

        var switched = _workspace.InitializeAsync(record.TargetPath).GetAwaiter().GetResult();
        if (!switched.IsSuccess)
        {
            record = Update(record with
            {
                State = WorkspaceMigrationState.RollbackRequired,
                ErrorCode = switched.Error!.Code,
                ErrorMessage = switched.Error.Message,
            });
            return Result<WorkspaceMigrationRecord>.Fail(
                new EclError(
                    switched.Error!.Code,
                    "文件已复制到新位置，但切换工作区配置失败。当前仍使用原工作区，可在确认后运行 workspace set 重新切换。",
                    switched.Error.Message));
        }

        record = Update(record with
        {
            State = WorkspaceMigrationState.Completed,
            ErrorCode = null,
            ErrorMessage = null,
        });

        _logger.LogInformation(
            "工作区迁移完成：{Source} → {Target}（{Files} 个文件）。",
            record.SourcePath,
            record.TargetPath,
            record.CopiedFiles);
        return Result<WorkspaceMigrationRecord>.Ok(record);
    }

    private static Result Verify(string source, string staging)
    {
        var sourceFiles = EnumerateFiles(source)
            .Select(file => (Relative: Path.GetRelativePath(source, file), Size: new FileInfo(file).Length))
            .ToArray();
        var stagingFiles = EnumerateFiles(staging)
            .Select(file => (Relative: Path.GetRelativePath(staging, file), Size: new FileInfo(file).Length))
            .ToDictionary(item => item.Relative, item => item.Size, StringComparer.OrdinalIgnoreCase);

        if (sourceFiles.Length != stagingFiles.Count)
        {
            return Result.Fail(
                "workspace.migration.file_count_mismatch",
                $"复制后的文件数量不一致：源 {sourceFiles.Length}，目标 {stagingFiles.Count}。");
        }

        foreach (var (relative, size) in sourceFiles)
        {
            if (!stagingFiles.TryGetValue(relative, out var copiedSize) || copiedSize != size)
            {
                return Result.Fail(
                    "workspace.migration.size_mismatch",
                    $"文件大小不一致：{relative}。");
            }

            if (size <= HashLimitBytes)
            {
                var sourceHash = Hash(Path.Combine(source, relative));
                var copiedHash = Hash(Path.Combine(staging, relative));
                if (!string.Equals(sourceHash, copiedHash, StringComparison.Ordinal))
                {
                    return Result.Fail(
                        "workspace.migration.hash_mismatch",
                        $"文件内容校验失败：{relative}。");
                }
            }
        }

        return Result.Ok();
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        return Directory.EnumerateFiles(root, "*", options);
    }

    private static (long Bytes, int Files, int Instances) Measure(string source, List<string> problems)
    {
        try
        {
            long bytes = 0;
            var files = 0;
            foreach (var file in EnumerateFiles(source))
            {
                bytes += new FileInfo(file).Length;
                files++;
            }

            var instances = Directory.Exists(Path.Combine(source, "instances"))
                ? Directory.EnumerateDirectories(Path.Combine(source, "instances")).Count()
                : 0;
            return (bytes, files, instances);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"无法读取源工作区：{exception.Message}");
            return (0, 0, 0);
        }
    }

    /// <summary>尽力检查世界会话锁；只作为“游戏可能仍在运行”的提示。</summary>
    private static bool HasLockedSessionFile(string source)
    {
        try
        {
            var saves = Path.Combine(source, "instances");
            if (!Directory.Exists(saves))
            {
                return false;
            }

            foreach (var sessionLock in Directory.EnumerateFiles(saves, "session.lock", SearchOption.AllDirectories).Take(256))
            {
                try
                {
                    using var stream = new FileStream(sessionLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // 权限不足不代表游戏在运行。
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private void TryDeleteStaging(string staging, string source, string target)
    {
        if (string.IsNullOrWhiteSpace(staging) ||
            string.Equals(staging, source, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(staging, target, StringComparison.OrdinalIgnoreCase) ||
            !staging.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "清理迁移 staging 失败：{Path}", staging);
        }
    }

    private WorkspaceMigrationRecord Update(WorkspaceMigrationRecord record)
    {
        var next = record with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        SaveRecord(next);
        Current = next;
        return next;
    }

    private void SaveRecord(WorkspaceMigrationRecord record)
    {
        var directory = Path.GetDirectoryName(_recordPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _recordPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, EclJson.Indented));
        File.Move(temporary, _recordPath, overwrite: true);
    }

    private WorkspaceMigrationRecord? LoadRecord()
    {
        if (!File.Exists(_recordPath))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<WorkspaceMigrationRecord>(
                File.ReadAllText(_recordPath),
                EclJson.Default);
            return record;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "迁移记录不可读：{Path}", _recordPath);
            return null;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes} B",
    };
}
