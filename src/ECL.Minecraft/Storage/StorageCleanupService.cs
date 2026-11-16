// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Storage;

public enum CleanupItemKind
{
    DownloadPart,
    RebuildableCache,
    InstanceLog,
    DiagnosticTemp,
}

public sealed record CleanupPreviewItem(
    string Id,
    CleanupItemKind Kind,
    string Path,
    string AllowedRoot,
    long Bytes,
    string? InstanceId,
    string Reason,
    string? CategoryId = null);

public sealed record CleanupPreview(
    IReadOnlyList<CleanupPreviewItem> Items,
    int SkippedCount,
    IReadOnlyList<string> Warnings)
{
    public long TotalBytes => Items.Sum(item => item.Bytes);

    public bool HasItems => Items.Count > 0;
}

public sealed record CleanupExecutionResult(
    int Deleted,
    int Skipped,
    int Failed,
    long BytesFreed,
    IReadOnlyList<string> Errors);

public sealed record StorageCleanupRequest
{
    public bool IncludeDownloadParts { get; init; } = true;

    public bool IncludeRebuildableCache { get; init; } = true;

    public bool IncludeInstanceLogs { get; init; } = true;

    public bool IncludeDiagnosticTemps { get; init; } = true;

    /// <summary>为空时处理所有 ECL 实例；指定时只处理该实例的日志。</summary>
    public string? InstanceId { get; init; }
}

/// <summary>
/// CLI 清理入口。F-07 起不再维护第二套白名单与保留期：预览直接读取
/// <see cref="IStorageInventoryService"/> 的分类结果，执行仍由同一个清理服务重新校验并删除。
/// 因此页面与 CLI 对“什么能删、什么时候能删、活动状态如何保护”给出同一结论。
/// </summary>
public sealed class StorageCleanupService(IStorageInventoryService inventory)
{
    private const int MaxEntriesPerCategory = 5_000;

    public async Task<CleanupPreview> PreviewAsync(
        StorageCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var inventoryResult = await inventory.ScanAsync(
            new StorageInventoryScanOptions { MaxEntriesPerCategory = MaxEntriesPerCategory },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var items = new List<CleanupPreviewItem>();
        var skipped = 0;
        foreach (var category in inventoryResult.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in category.Entries)
            {
                var kind = SelectKind(request, entry);
                if (kind is null)
                {
                    continue;
                }

                if (!entry.CanClean)
                {
                    skipped++;
                    continue;
                }

                items.Add(new CleanupPreviewItem(
                    entry.Id,
                    kind.Value,
                    entry.FullPath,
                    entry.AllowedRoot,
                    entry.Size ?? 0,
                    entry.InstanceId,
                    DescribeRule(entry),
                    entry.CategoryId));
            }
        }

        var warnings = inventoryResult.Warnings.ToList();
        if (!inventoryResult.IsComplete)
        {
            warnings.Add("存储扫描不完整；本次清理预览只包含已扫描到的项目。");
        }

        return new CleanupPreview(
            items
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            skipped,
            warnings);
    }

    /// <summary>按请求类型与分类白名单选择条目；未知分类不进入 CLI 清理。</summary>
    private static CleanupItemKind? SelectKind(StorageCleanupRequest request, StorageFileEntry entry)
    {
        // InstanceId 只约束实例日志；ECL 自有缓存不属于某个实例，不因 --instance 被排除。
        if (entry.CategoryId == "instance.logs" &&
            request.InstanceId is { Length: > 0 } instanceId &&
            !string.Equals(entry.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return null;
        }

        return entry.CategoryId switch
        {
            "ecl.downloads" when request.IncludeDownloadParts &&
                                  entry.FileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) =>
                CleanupItemKind.DownloadPart,
            "ecl.imageCache" or
                "ecl.metadataCache" or
                "ecl.installTemp" or
                "ecl.updateLeftovers" or
                "ecl.tempFiles" when request.IncludeRebuildableCache =>
                CleanupItemKind.RebuildableCache,
            "instance.logs" when request.IncludeInstanceLogs =>
                CleanupItemKind.InstanceLog,
            "ecl.diagnostics" when request.IncludeDiagnosticTemps &&
                                   string.Equals(entry.CleanupRule, "diagnosticTemp", StringComparison.Ordinal) =>
                CleanupItemKind.DiagnosticTemp,
            _ => null,
        };
    }

    private static string DescribeRule(StorageFileEntry entry) => entry.CleanupRule switch
    {
        "staleDownloadPart" => "超过保留期且没有被活动下载引用的 .part 临时产物。",
        "instanceLog" => "超过保留期的实例历史日志。",
        "diagnosticTemp" => "诊断导出留下的临时文件。",
        _ => "ECL 可重新生成的缓存文件。",
    };

    public async Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var entries = new List<StorageFileEntry>(preview.Items.Count);
        foreach (var item in preview.Items)
        {
            var categoryId = ResolveCategoryId(item);
            var spec = StorageCategorySpec.All.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, categoryId, StringComparison.Ordinal));
            if (spec is null)
            {
                // 没有分类的条目不能重建成领域条目；执行阶段按“未可删除”跳过，不猜一个分类。
                continue;
            }

            entries.Add(new StorageFileEntry
            {
                Id = item.Id,
                CategoryId = spec.Id,
                Group = spec.Group,
                FullPath = item.Path,
                AllowedRoot = item.AllowedRoot,
                Size = item.Bytes,
                InstanceId = item.InstanceId,
                CanClean = true,
            });
        }

        var deleteResult = await inventory
            .DeleteAsync(entries, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 路径逃逸/重解析点属于安全拒绝，按失败报告而不是“跳过”，避免被当成可延后的普通情况。
        var securityViolations = 0;
        var errors = new List<string>();
        foreach (var failed in deleteResult.Items.Where(result =>
                     result.State is StorageDeleteItemState.Failed))
        {
            errors.Add($"删除失败：{failed.Entry.FullPath}（{failed.Reason}）");
        }

        foreach (var skippedItem in deleteResult.Items.Where(result =>
                     result.State is StorageDeleteItemState.Skipped))
        {
            if (skippedItem.Reason is "outsideAllowedRoot" or "reparsePoint")
            {
                securityViolations++;
                errors.Add($"拒绝越界清理：{skippedItem.Entry.FullPath}");
                continue;
            }

            errors.Add($"跳过：{skippedItem.Entry.FullPath}（{skippedItem.Reason}）");
        }

        // .part 的伴随元数据（.part.json/.part.lock）不是清理条目；成功删除后尽力清掉。
        foreach (var item in preview.Items.Where(item => item.Kind == CleanupItemKind.DownloadPart))
        {
            TryDelete(DownloadArtifactLifecycle.MetadataPath(item.Path));
        }

        return new CleanupExecutionResult(
            deleteResult.Succeeded,
            deleteResult.Skipped - securityViolations + (preview.Items.Count - entries.Count),
            deleteResult.Failed + securityViolations,
            deleteResult.BytesFreed,
            errors);
    }

    /// <summary>旧调用方（CLI/测试）可能只给 Kind；映射到唯一策略使用的分类 ID。</summary>
    private static string ResolveCategoryId(CleanupPreviewItem item) => item.CategoryId ?? item.Kind switch
    {
        CleanupItemKind.DownloadPart => "ecl.downloads",
        CleanupItemKind.InstanceLog => "instance.logs",
        CleanupItemKind.DiagnosticTemp => "ecl.diagnostics",
        _ => "ecl.tempFiles",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 元数据残留由后续清理周期处理。
        }
    }
}
