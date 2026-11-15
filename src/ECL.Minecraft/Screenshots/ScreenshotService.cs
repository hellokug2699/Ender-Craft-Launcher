// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Screenshots;

/// <summary>一张游戏截图的只读元数据。路径固定为实例游戏目录内的相对路径。</summary>
public sealed record ScreenshotEntry(
    string FileName,
    string RelativePath,
    long Size,
    DateTimeOffset ModifiedAt,
    bool IsRecognizedImage);

/// <summary>截图目录的一次读取结果；单个文件不可访问时计入 <see cref="SkippedCount"/>，不把未知当成空。</summary>
public sealed record ScreenshotList(
    IReadOnlyList<ScreenshotEntry> Items,
    int SkippedCount,
    string? Warning = null);

/// <summary>
/// 截图目录读取、受控读取与删除（ADR 0042/0047）。页面不直接拼接路径或访问文件系统：
/// 所有读取都以实例游戏目录为边界，删除走实例事务协调器并返回可判定的结果。
/// </summary>
public interface IScreenshotService
{
    /// <summary>读取 <c>screenshots/</c> 下直接包含的图片文件（PNG/JPEG）。目录不存在时返回空列表。</summary>
    Task<Result<ScreenshotList>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取一张截图的原始字节供页面解码。只接受本服务列出的 <c>screenshots/</c> 直接子文件，
    /// 并限制读取上限，避免超大文件或路径逃逸。
    /// </summary>
    Task<Result<byte[]>> ReadImageAsync(
        string instanceId,
        string relativePath,
        int maxBytes = ScreenshotService.MaxReadBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除选中的截图。全部删除在同一个实例事务中执行：失败时由事务协调器回滚，
    /// 不向页面报告“部分成功已回滚”以外的猜测结果。
    /// </summary>
    Task<Result<ScreenshotDeleteReport>> DeleteAsync(
        string instanceId,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);
}

/// <summary>删除结果；<see cref="RecoveryRequired"/> 为 true 时事务无法自动恢复，页面必须如实提示。</summary>
public sealed record ScreenshotDeleteReport(
    IReadOnlyList<string> DeletedRelativePaths,
    bool RecoveryRequired,
    string? Detail = null);

public sealed class ScreenshotService : IScreenshotService
{
    /// <summary>单张截图读取上限。4K PNG 通常远小于此值；超过时拒绝解码而不是整页卡死。</summary>
    public const int MaxReadBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
    };

    private readonly IInstanceService _instances;
    private readonly InstanceTransactionCoordinator _transactions;
    private readonly IAppPaths _paths;
    private readonly ILogger<ScreenshotService> _logger;

    public ScreenshotService(
        IInstanceService instances,
        InstanceTransactionCoordinator transactions,
        IAppPaths paths,
        ILogger<ScreenshotService>? logger = null)
    {
        _instances = instances;
        _transactions = transactions;
        _paths = paths;
        _logger = logger ?? NullLogger<ScreenshotService>.Instance;
    }

    public async Task<Result<ScreenshotList>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await OpenWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Result<ScreenshotList>.Fail(workspace.Error!);
        }

        var root = workspace.Value!;
        var directory = Path.Combine(root.RootPath, "screenshots");
        if (!Directory.Exists(directory))
        {
            return Result<ScreenshotList>.Ok(new ScreenshotList([], 0));
        }

        var items = new List<ScreenshotEntry>();
        var skipped = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path);
                if (!ImageExtensions.Contains(extension))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 截图页不跟随链接，避免读取实例目录之外的文件。
                        skipped++;
                        continue;
                    }

                    var info = new FileInfo(path);
                    var relative = Path.GetRelativePath(root.RootPath, path);
                    items.Add(new ScreenshotEntry(
                        info.Name,
                        relative,
                        info.Length,
                        info.LastWriteTimeUtc,
                        IsRecognizedImage(info.FullName)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取实例截图目录失败：{InstanceId}", instanceId);
            return Result<ScreenshotList>.Fail(
                "screenshots.list_failed",
                "读取截图目录失败；目录可能不可访问。",
                exception.Message);
        }

        return Result<ScreenshotList>.Ok(new ScreenshotList(
            items,
            skipped,
            skipped == 0 ? null : $"有 {skipped} 个文件无法读取，未计入截图列表。"));
    }

    public async Task<Result<byte[]>> ReadImageAsync(
        string instanceId,
        string relativePath,
        int maxBytes = MaxReadBytes,
        CancellationToken cancellationToken = default)
    {
        var workspace = await OpenWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Result<byte[]>.Fail(workspace.Error!);
        }

        var root = workspace.Value!;
        if (!TryNormalizeScreenshotPath(root, relativePath, out var normalized, out var error))
        {
            return Result<byte[]>.Fail("screenshots.invalid_path", error);
        }

        return await root.ReadBytesAsync(normalized, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ScreenshotDeleteReport>> DeleteAsync(
        string instanceId,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
        {
            return Result<ScreenshotDeleteReport>.Fail("screenshots.empty_selection", "没有选择要删除的截图。");
        }

        var workspace = await OpenWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Result<ScreenshotDeleteReport>.Fail(workspace.Error!);
        }

        var root = workspace.Value!;
        var operations = new List<InstanceChangeOperation>(relativePaths.Count);
        var normalizedPaths = new List<string>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            if (!TryNormalizeScreenshotPath(root, relativePath, out var normalized, out var error))
            {
                return Result<ScreenshotDeleteReport>.Fail("screenshots.invalid_path", error);
            }

            var full = root.Resolve(normalized);
            if (!File.Exists(full))
            {
                return Result<ScreenshotDeleteReport>.Fail(
                    "screenshots.not_found",
                    $"截图不存在或已被移动：{normalized}");
            }

            normalizedPaths.Add(normalized);
            operations.Add(InstanceChangeOperation.Delete(normalized));
        }

        var transaction = await _transactions.ExecuteAsync(
            instanceId,
            new InstanceChangePlan(operations, $"删除 {operations.Count} 张截图"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!transaction.IsSuccess)
        {
            return Result<ScreenshotDeleteReport>.Fail(transaction.Error!);
        }

        var record = transaction.Value!;
        var recoveryRequired = record.State is InstanceTransactionState.NeedsRecovery
            or InstanceTransactionState.RollingBack
            or InstanceTransactionState.RolledBack
            or InstanceTransactionState.Failed;
        if (recoveryRequired)
        {
            return Result<ScreenshotDeleteReport>.Fail(
                "screenshots.delete_incomplete",
                "删除没有完整提交，需要恢复处理。",
                record.ErrorMessage ?? record.State.ToString());
        }

        _logger.LogInformation(
            "已删除实例 {InstanceId} 的 {Count} 张截图，事务 {TransactionId}。",
            instanceId,
            normalizedPaths.Count,
            record.TransactionId);
        return Result<ScreenshotDeleteReport>.Ok(
            new ScreenshotDeleteReport(normalizedPaths, RecoveryRequired: false));
    }

    private async Task<Result<InstanceFileWorkspace>> OpenWorkspaceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceFileWorkspace>.Fail("instance.not_found", "实例不存在。");
        }

        var rootPath = InstancePaths.GetGameDirectory(_paths, instance);
        if (!Directory.Exists(rootPath))
        {
            return Result<InstanceFileWorkspace>.Fail(
                "screenshots.root_missing",
                "实例游戏目录不存在或当前不可访问。");
        }

        return Result<InstanceFileWorkspace>.Ok(new InstanceFileWorkspace(rootPath));
    }

    /// <summary>
    /// 只接受 <c>screenshots/&lt;文件名&gt;</c> 形式的直接子文件。任何子目录、相对段或
    /// 非图片扩展名都会被拒绝，避免该服务被当成通用文件删除入口。
    /// </summary>
    private static bool TryNormalizeScreenshotPath(
        InstanceFileWorkspace root,
        string? relativePath,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "截图路径不能为空。";
            return false;
        }

        try
        {
            normalized = root.NormalizeRelative(relativePath);
        }
        catch (InstancePathException exception)
        {
            error = exception.Message;
            return false;
        }

        var separator = Path.DirectorySeparatorChar;
        var parts = normalized.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "screenshots", StringComparison.OrdinalIgnoreCase))
        {
            error = "只能操作截图目录中的直接文件。";
            return false;
        }

        if (!ImageExtensions.Contains(Path.GetExtension(normalized)))
        {
            error = "目标不是支持的图片文件。";
            return false;
        }

        return true;
    }

    /// <summary>按文件签名确认它确实是图片；扩展名只是提示（ADR 0042 §9）。</summary>
    private static bool IsRecognizedImage(string fullPath)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(header);
            if (read < 3)
            {
                return false;
            }

            var png = read >= 8 &&
                      header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
            var jpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            return png || jpeg;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
