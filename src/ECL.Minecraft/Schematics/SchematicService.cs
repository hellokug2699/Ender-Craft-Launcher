// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Schematics;

/// <summary>
/// 页面能够识别的投影文件格式（schematics.md §3）。
/// 格式识别与“当前实例是否具备加载该格式的能力”是两个独立问题，见
/// <see cref="SchematicSupportService"/>。
/// </summary>
public enum SchematicFormat
{
    Litematica,
    SpongeSchematic,
    MceSchematic,
    NbtStructure,
    AxiomBlueprint,
}

/// <summary>
/// 文件格式的识别状态。扩展名只能作为提示；没有匹配到可验证签名时标记为
/// <see cref="Unverified"/>，不把“扩展名正确”当成“格式已确认”（ADR 0042 §9）。
/// </summary>
public enum SchematicRecognition
{
    Recognized,
    Unverified,
    Unreadable,
}

/// <summary>一个投影文件的只读元数据。</summary>
public sealed record SchematicEntry(
    string FileName,
    string RelativePath,
    SchematicFormat Format,
    SchematicRecognition Recognition,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record SchematicList(
    IReadOnlyList<SchematicEntry> Items,
    int SkippedCount,
    string? Warning = null);

public sealed record SchematicDeleteReport(
    IReadOnlyList<string> DeletedRelativePaths,
    bool RecoveryRequired,
    string? Detail = null);

/// <summary>
/// 投影文件发现、格式识别与受控删除（schematics.md §2–§4、ADR 0042/0047）。
/// <para>
/// 扫描范围只包含投影工具实际使用的目录：<c>schematics/</c>（Litematica / Create /
/// MCEdit）、<c>config/worldedit/schematics/</c>（WorldEdit）与
/// <c>config/axiom/blueprints/</c>（Axiom）。页面不自行拼接路径，也不把文件系统
/// 浏览能力暴露给 UI。
/// </para>
/// </summary>
public interface ISchematicService
{
    Task<Result<SchematicList>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>删除选中的投影文件；删除在同一个实例事务中执行，失败时不谎报部分成功。</summary>
    Task<Result<SchematicDeleteReport>> DeleteAsync(
        string instanceId,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);
}

public sealed class SchematicService : ISchematicService
{
    public const int MaxEntries = 5000;

    private static readonly Dictionary<string, SchematicFormat> FormatsByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".litematic"] = SchematicFormat.Litematica,
            [".schem"] = SchematicFormat.SpongeSchematic,
            [".schematic"] = SchematicFormat.MceSchematic,
            [".nbt"] = SchematicFormat.NbtStructure,
            [".bp"] = SchematicFormat.AxiomBlueprint,
        };

    private static readonly (string Id, string RelativePath)[] Roots =
    [
        ("schematics", "schematics"),
        ("worldedit", Path.Combine("config", "worldedit", "schematics")),
        ("axiom", Path.Combine("config", "axiom", "blueprints")),
    ];

    private readonly IInstanceService _instances;
    private readonly InstanceTransactionCoordinator _transactions;
    private readonly IAppPaths _paths;
    private readonly ILogger<SchematicService> _logger;

    public SchematicService(
        IInstanceService instances,
        InstanceTransactionCoordinator transactions,
        IAppPaths paths,
        ILogger<SchematicService>? logger = null)
    {
        _instances = instances;
        _transactions = transactions;
        _paths = paths;
        _logger = logger ?? NullLogger<SchematicService>.Instance;
    }

    public async Task<Result<SchematicList>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await OpenWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Result<SchematicList>.Fail(workspace.Error!);
        }

        var root = workspace.Value!;
        var items = new List<SchematicEntry>();
        var skipped = 0;
        var truncated = false;

        foreach (var (_, relativeRoot) in Roots)
        {
            var directory = root.Resolve(relativeRoot);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (items.Count >= MaxEntries)
                        {
                            truncated = true;
                            pending.Clear();
                            break;
                        }

                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                // 不跟随链接，避免把实例目录之外的文件当成投影。
                                continue;
                            }

                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                pending.Push(path);
                                continue;
                            }

                            if (!FormatsByExtension.TryGetValue(Path.GetExtension(path), out var format))
                            {
                                continue;
                            }

                            var info = new FileInfo(path);
                            var relative = Path.GetRelativePath(root.RootPath, path);
                            items.Add(new SchematicEntry(
                                info.Name,
                                relative,
                                format,
                                DetectRecognition(info.FullName, format),
                                info.Length,
                                info.LastWriteTimeUtc));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            skipped++;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        var warning = skipped > 0
            ? $"有 {skipped} 个文件或目录无法读取，未计入投影列表。"
            : null;
        if (truncated)
        {
            warning = string.IsNullOrEmpty(warning)
                ? $"投影文件超过 {MaxEntries} 个，列表不完整。"
                : $"{warning} 投影文件超过 {MaxEntries} 个，列表不完整。";
        }

        return Result<SchematicList>.Ok(new SchematicList(items, skipped, warning));
    }

    public async Task<Result<SchematicDeleteReport>> DeleteAsync(
        string instanceId,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
        {
            return Result<SchematicDeleteReport>.Fail("schematics.empty_selection", "没有选择要删除的投影文件。");
        }

        var workspace = await OpenWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return Result<SchematicDeleteReport>.Fail(workspace.Error!);
        }

        var root = workspace.Value!;
        var operations = new List<InstanceChangeOperation>(relativePaths.Count);
        var normalizedPaths = new List<string>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            if (!TryNormalizeSchematicPath(root, relativePath, out var normalized, out var error))
            {
                return Result<SchematicDeleteReport>.Fail("schematics.invalid_path", error);
            }

            var full = root.Resolve(normalized);
            if (!File.Exists(full))
            {
                return Result<SchematicDeleteReport>.Fail(
                    "schematics.not_found",
                    $"投影文件不存在或已被移动：{normalized}");
            }

            normalizedPaths.Add(normalized);
            operations.Add(InstanceChangeOperation.Delete(normalized));
        }

        var transaction = await _transactions.ExecuteAsync(
            instanceId,
            new InstanceChangePlan(operations, $"删除 {operations.Count} 个投影文件"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!transaction.IsSuccess)
        {
            return Result<SchematicDeleteReport>.Fail(transaction.Error!);
        }

        var record = transaction.Value!;
        if (record.State is InstanceTransactionState.NeedsRecovery or InstanceTransactionState.RollingBack
            or InstanceTransactionState.RolledBack or InstanceTransactionState.Failed)
        {
            return Result<SchematicDeleteReport>.Fail(
                "schematics.delete_incomplete",
                "删除没有完整提交，需要恢复处理。",
                record.ErrorMessage ?? record.State.ToString());
        }

        _logger.LogInformation(
            "已删除实例 {InstanceId} 的 {Count} 个投影文件，事务 {TransactionId}。",
            instanceId,
            normalizedPaths.Count,
            record.TransactionId);
        return Result<SchematicDeleteReport>.Ok(
            new SchematicDeleteReport(normalizedPaths, RecoveryRequired: false));
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
                "schematics.root_missing",
                "实例游戏目录不存在或当前不可访问。");
        }

        return Result<InstanceFileWorkspace>.Ok(new InstanceFileWorkspace(rootPath));
    }

    /// <summary>只接受扫描根目录下的已知格式文件；拒绝根目录之外的路径。</summary>
    private static bool TryNormalizeSchematicPath(
        InstanceFileWorkspace root,
        string? relativePath,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "投影文件路径不能为空。";
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

        if (!FormatsByExtension.ContainsKey(Path.GetExtension(normalized)))
        {
            error = "目标不是已识别的投影文件扩展名。";
            return false;
        }

        foreach (var (_, relativeRoot) in Roots)
        {
            var rootWithSeparator = relativeRoot + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        error = "只能操作投影目录中的文件。";
        return false;
    }

    private static SchematicRecognition DetectRecognition(string fullPath, SchematicFormat format)
    {
        if (format == SchematicFormat.AxiomBlueprint)
        {
            // Axiom 蓝图没有公开、稳定的可验证签名；只按扩展名识别，页面必须显示“未验证”。
            return IsReadable(fullPath) ? SchematicRecognition.Unverified : SchematicRecognition.Unreadable;
        }

        try
        {
            Span<byte> header = stackalloc byte[2];
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(header);
            if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
            {
                // gzip 容器：Litematica、WorldEdit Sponge/MCEdit 与 Create 均使用压缩 NBT。
                return SchematicRecognition.Recognized;
            }

            // 未压缩 NBT 的根标签是 TAG_Compound (0x0A)。
            if (read >= 1 && header[0] == 0x0A)
            {
                return SchematicRecognition.Recognized;
            }

            return SchematicRecognition.Unverified;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SchematicRecognition.Unreadable;
        }
    }

    private static bool IsReadable(string fullPath)
    {
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
