// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Export;

/// <summary>导出方式（export.md §2）。</summary>
public enum InstanceExportMode
{
    Full,
    Custom,
    Diagnostics,
}

/// <summary>内容类别的隐私/敏感性提示类型；页面按类别显示对应风险提示（export.md §6）。</summary>
public enum ExportRiskKind
{
    None,
    Worlds,
    Screenshots,
    Schematics,
    Logs,
    Config,
}

/// <summary>一个实例内容类别的扫描摘要。</summary>
public sealed record ExportCategorySummary(
    string Id,
    int FileCount,
    long TotalBytes,
    int InaccessibleCount,
    bool Complete);

/// <summary>导出前的类别与体积概览。未知大小不计为 0；扫描不完整时明确标记。</summary>
public sealed record InstanceExportPlan(
    IReadOnlyList<ExportCategorySummary> Categories,
    int FileCount,
    long TotalBytes,
    int ExcludedFileCount,
    bool Complete,
    string? Warning = null);

public enum ExportSkipReason
{
    Unreadable,
    MetadataUnreadable,
    TooManyFiles,
    SourceChanged,
}

public sealed record ExportSkippedFile(string RelativePath, ExportSkipReason Reason, string? Detail = null);

/// <summary>一次导出的真实结果；部分文件被跳过时 <see cref="IsComplete"/> 为 false。</summary>
public sealed record InstanceExportReport(
    string OutputPath,
    string OutputDirectory,
    long Bytes,
    int FileCount,
    int ExcludedFileCount,
    IReadOnlyList<ExportSkippedFile> Skipped,
    bool IsComplete,
    bool IsDiagnostics,
    int DiagnosticEntries,
    string? ManifestEntry);

/// <summary>导出进度。字节与文件数量分开，避免用文件数冒充压缩进度。</summary>
public sealed record InstanceExportProgress(
    string StageId,
    int Completed,
    int Total,
    long BytesCompleted,
    long TotalBytes,
    string? CurrentItem = null);

public sealed record ExportRequest(InstanceExportMode Mode, IReadOnlySet<string>? Categories = null);

/// <summary>
/// 实例整体导出与诊断包导出（export.md、ADR 0015/0018/0042/0047/0048）。
/// <para>
/// 输出固定写入实例游戏目录下的 <c>exports/</c>，不提供用户自定义目标；
/// 完整/自定义导出使用可携带的 zip（<c>manifest.json</c> + <c>instance/</c> +
/// <c>minecraft/</c>），诊断包复用既有 <see cref="IDiagnosticPackageService"/>。
/// 登录状态、令牌、API Key 与私钥属于永久排除项，任何导出方式都不能包含。
/// </para>
/// </summary>
public interface IInstanceExportService
{
    /// <summary>扫描实例内容并给出分类、文件数与未压缩体积；不创建任何文件。</summary>
    Task<Result<InstanceExportPlan>> PlanAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>执行导出。输出固定位于实例目录的 <c>exports/</c> 下。</summary>
    Task<Result<InstanceExportReport>> ExportAsync(
        string instanceId,
        ExportRequest request,
        IProgress<InstanceExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class InstanceExportService : IInstanceExportService
{
    public const string ExportsFolderName = "exports";
    public const int MaxFiles = 200_000;

    private static readonly InstanceExportCategoryDefinition[] CategoryDefinitions =
    [
        new("worlds", ["saves"], [], ExportRiskKind.Worlds),
        new("screenshots", ["screenshots"], [], ExportRiskKind.Screenshots),
        new(
            "schematics",
            ["schematics", Path.Combine("config", "worldedit", "schematics"), Path.Combine("config", "axiom", "blueprints")],
            [],
            ExportRiskKind.Schematics),
        new("logs", ["logs", "crash-reports"], [], ExportRiskKind.Logs),
        new(
            "config",
            ["config", "defaultconfigs"],
            ["options.txt", "optionsshaders.txt", "servers.dat"],
            ExportRiskKind.Config),
        new("mods", ["mods"], [], ExportRiskKind.None),
        new("resourcePacks", ["resourcepacks"], [], ExportRiskKind.None),
        new("shaderPacks", ["shaderpacks"], [], ExportRiskKind.None),
        new("other", [], [], ExportRiskKind.None),
    ];

    /// <summary>永久排除的文件名（不区分大小写）。这些内容可能代表登录状态或凭据，不得进入任何导出包。</summary>
    private static readonly string[] ExcludedFileNames =
    [
        "launcher_accounts.json",
        "launcher_accounts.json.bak",
        "launcher_profiles.json",
        "launcher_profiles.json.bak",
        "launcher_profiles_microsoft_store.json",
        "launcher_msa_credentials.bin",
        "launcher_msa_credentials.bin.bak",
        "usercache.json",
        "credentials.json",
        "credentials.dat",
    ];

    /// <summary>永久排除的扩展名：私钥、证书私钥容器与常见的令牌文件。</summary>
    private static readonly string[] ExcludedExtensions =
    [
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".accesstoken",
        ".refreshtoken",
    ];

    private readonly IInstanceService _instances;
    private readonly IDiagnosticPackageService _diagnostics;
    private readonly IAppPaths _paths;
    private readonly IOperationService? _operations;
    private readonly ILogger<InstanceExportService> _logger;

    public InstanceExportService(
        IInstanceService instances,
        IDiagnosticPackageService diagnostics,
        IAppPaths paths,
        IOperationService? operations = null,
        ILogger<InstanceExportService>? logger = null)
    {
        _instances = instances;
        _diagnostics = diagnostics;
        _paths = paths;
        _operations = operations;
        _logger = logger ?? NullLogger<InstanceExportService>.Instance;
    }

    public async Task<Result<InstanceExportPlan>> PlanAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var context = await OpenContextAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!context.IsSuccess)
        {
            return Result<InstanceExportPlan>.Fail(context.Error!);
        }

        var (instance, root) = context.Value!;
        try
        {
            var scan = await Task.Run(
                () => Scan(instance, root, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return Result<InstanceExportPlan>.Ok(BuildPlan(scan));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "扫描实例导出内容失败：{InstanceId}", instanceId);
            return Result<InstanceExportPlan>.Fail("export.plan_failed", "扫描实例内容失败。", exception.Message);
        }
    }

    public async Task<Result<InstanceExportReport>> ExportAsync(
        string instanceId,
        ExportRequest request,
        IProgress<InstanceExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await OpenContextAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!context.IsSuccess)
        {
            return Result<InstanceExportReport>.Fail(context.Error!);
        }

        var (instance, root) = context.Value!;
        if (request.Mode == InstanceExportMode.Diagnostics)
        {
            return await ExportDiagnosticsAsync(instance, root, progress, cancellationToken).ConfigureAwait(false);
        }

        var output = BuildOutputPath(instance, root, request.Mode == InstanceExportMode.Diagnostics);
        using var operation = CreateOperation(instance, request.Mode, progress);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.AttachControl(cancellation);

        try
        {
            var report = await Task.Run(
                () => ExportArchive(instance, root, request, output, progress, cancellation.Token),
                cancellation.Token).ConfigureAwait(false);
            operation.Complete(new OperationProgressUpdate
            {
                StageId = "export-finish",
                Completed = report.FileCount,
                Total = report.FileCount,
                BytesDownloaded = report.Bytes,
                TotalBytes = report.Bytes,
                CurrentItem = Path.GetFileName(report.OutputPath),
            });
            return Result<InstanceExportReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            operation.Cancel();
            return Result<InstanceExportReport>.Fail("export.cancelled", "导出已取消。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(output);
            operation.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                "export.failed",
                "导出失败。",
                exception.Message));
            _logger.LogError(exception, "导出实例失败：{InstanceId} -> {Output}", instanceId, output);
            return Result<InstanceExportReport>.Fail("export.failed", "导出失败。", exception.Message);
        }
        finally
        {
            // OperationProgressBridge 的 Dispose 会把未结束的任务标记为取消；
            // 成功/失败/取消路径已经显式结束它。
        }
    }

    private async Task<Result<InstanceExportReport>> ExportDiagnosticsAsync(
        GameInstance instance,
        string root,
        IProgress<InstanceExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var output = BuildOutputPath(instance, root, diagnostics: true);
        using var operation = CreateOperation(instance, InstanceExportMode.Diagnostics, progress);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.AttachControl(cancellation);

        try
        {
            progress?.Report(new InstanceExportProgress("export-diagnostics", 0, 1, 0, 0));
            var result = await _diagnostics
                .ExportAsync(instance.Id, output, cancellation.Token)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                operation.Fail(new OperationError(
                    OperationErrorKind.LocalIO,
                    result.Error!.Code,
                    result.Error.Message,
                    result.Error.Detail));
                return Result<InstanceExportReport>.Fail(result.Error!);
            }

            var report = result.Value!;
            var directory = Path.GetDirectoryName(report.OutputPath) ?? Path.GetDirectoryName(output)!;
            progress?.Report(new InstanceExportProgress("export-finish", 1, 1, report.Bytes, report.Bytes));
            operation.Complete(new OperationProgressUpdate
            {
                StageId = "export-finish",
                Completed = 1,
                Total = 1,
                BytesDownloaded = report.Bytes,
                TotalBytes = report.Bytes,
                CurrentItem = Path.GetFileName(report.OutputPath),
            });
            return Result<InstanceExportReport>.Ok(new InstanceExportReport(
                report.OutputPath,
                directory,
                report.Bytes,
                FileCount: 0,
                ExcludedFileCount: 0,
                Skipped: [],
                IsComplete: true,
                IsDiagnostics: true,
                DiagnosticEntries: report.Entries,
                ManifestEntry: null));
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            operation.Cancel();
            return Result<InstanceExportReport>.Fail("export.cancelled", "导出已取消。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(output);
            operation.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                "export.failed",
                "导出诊断包失败。",
                exception.Message));
            return Result<InstanceExportReport>.Fail("export.failed", "导出诊断包失败。", exception.Message);
        }
    }

    private OperationProgressBridge<InstanceExportProgress> CreateOperation(
        GameInstance instance,
        InstanceExportMode mode,
        IProgress<InstanceExportProgress>? progress) =>
        new(
            _operations,
            new OperationStart
            {
                Kind = OperationKind.Export,
                Title = instance.Name,
                StageId = mode == InstanceExportMode.Diagnostics ? "export-diagnostics" : "export-scan",
                Source = "ECL",
                CanCancel = true,
                CanRetry = false,
            },
            progress,
            value => new OperationProgressUpdate
            {
                StageId = value.StageId,
                Completed = value.Completed,
                Total = value.Total > 0 ? value.Total : null,
                BytesDownloaded = value.BytesCompleted,
                TotalBytes = value.TotalBytes > 0 ? value.TotalBytes : null,
                CurrentItem = value.CurrentItem,
            });

    private InstanceExportReport ExportArchive(
        GameInstance instance,
        string root,
        ExportRequest request,
        string output,
        IProgress<InstanceExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scan = Scan(instance, root, cancellationToken, progress);
        var selected = ResolveCategoryIds(request);
        var files = scan.Files.Where(file => selected.Contains(file.CategoryId)).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("没有可导出的文件。");
        }

        var totalBytes = files.Sum(file => file.Size);
        progress?.Report(new InstanceExportProgress(
            "export-scan",
            files.Length,
            files.Length,
            totalBytes,
            totalBytes));

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var skipped = new List<ExportSkippedFile>(scan.Skipped);
        var completed = 0;
        long completedBytes = 0;

        using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            AddInstanceMetadata(archive, instance, _paths, skipped);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryName = "minecraft/" + file.RelativePath;
                try
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    using var source = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 81920,
                        useAsync: false);
                    using var target = entry.Open();
                    var copied = CopyWithProgress(source, target, cancellationToken);
                    if (copied != file.Size)
                    {
                        skipped.Add(new ExportSkippedFile(
                            file.RelativePath,
                            ExportSkipReason.SourceChanged,
                            $"扫描时为 {file.Size} 字节，导出时读取到 {copied} 字节。"));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new ExportSkippedFile(
                        file.RelativePath,
                        ExportSkipReason.Unreadable,
                        exception.Message));
                }

                completed++;
                completedBytes += file.Size;
                progress?.Report(new InstanceExportProgress(
                    "export-pack",
                    completed,
                    files.Length,
                    completedBytes,
                    totalBytes,
                    file.RelativePath));
            }

            var manifest = BuildManifest(instance, request, selected, files, scan, skipped);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            using (var stream = manifestEntry.Open())
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(manifest);
            }

            progress?.Report(new InstanceExportProgress(
                "export-finish",
                completed,
                files.Length,
                completedBytes,
                totalBytes));
        }

        var bytes = new FileInfo(output).Length;
        var isComplete = scan.Complete && skipped.Count == 0;
        _logger.LogInformation(
            "已导出实例 {InstanceId}（{Mode}）：{Output}，{Files} 个文件，{Bytes} 字节，跳过 {Skipped}。",
            instance.Id,
            request.Mode,
            output,
            files.Length,
            bytes,
            skipped.Count);
        return new InstanceExportReport(
            output,
            Path.GetDirectoryName(output)!,
            bytes,
            files.Length,
            scan.ExcludedFileCount,
            skipped,
            isComplete,
            IsDiagnostics: false,
            DiagnosticEntries: 0,
            ManifestEntry: "manifest.json");
    }

    private static long CopyWithProgress(
        Stream source,
        Stream target,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            target.Write(buffer, 0, read);
            copied += read;
        }

        return copied;
    }

    private static void AddInstanceMetadata(
        ZipArchive archive,
        GameInstance instance,
        IAppPaths paths,
        List<ExportSkippedFile> skipped)
    {
        var metadataFiles = new (string EntryName, string Path)[]
        {
            ("instance/instance.json", paths.GetInstanceMetadataFile(instance.Id)),
            ("instance/settings.json", paths.GetInstanceSettingsFile(instance.Id)),
        };
        foreach (var (entryName, path) in metadataFiles)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var target = entry.Open();
                source.CopyTo(target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new ExportSkippedFile(
                    entryName,
                    ExportSkipReason.MetadataUnreadable,
                    exception.Message));
            }
        }
    }

    private static string BuildManifest(
        GameInstance instance,
        ExportRequest request,
        IReadOnlySet<string> selected,
        IReadOnlyList<ExportFile> files,
        ScanResult scan,
        IReadOnlyList<ExportSkippedFile> skipped)
    {
        var manifest = new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow.ToString("O"),
            launcherVersion = typeof(InstanceExportService).Assembly.GetName().Version?.ToString(3),
            mode = request.Mode.ToString(),
            instance = new
            {
                instance.Id,
                instance.Name,
                instance.GameVersion,
                instance.VersionId,
                instance.Loader,
                instance.LoaderVersion,
                instance.FabricApiVersion,
                instance.OptiFineVersion,
            },
            categories = selected.Order(StringComparer.Ordinal).ToArray(),
            fileCount = files.Count,
            totalBytes = files.Sum(file => file.Size),
            excludedFileCount = scan.ExcludedFileCount,
            complete = scan.Complete && skipped.Count == 0,
            skipped = skipped.Select(item => new
            {
                path = item.RelativePath,
                reason = item.Reason.ToString(),
                detail = item.Detail,
            }),
            permanentExclusions = new
            {
                fileNames = ExcludedFileNames,
                extensions = ExcludedExtensions,
                note = "Login state, account tokens, API keys, private keys and equivalent secrets are never exported.",
            },
        };

        return JsonSerializer.Serialize(manifest, EclJson.Indented);
    }

    private static IReadOnlySet<string> ResolveCategoryIds(ExportRequest request)
    {
        if (request.Mode == InstanceExportMode.Full)
        {
            return new HashSet<string>(CategoryDefinitions.Select(definition => definition.Id), StringComparer.Ordinal);
        }

        if (request.Categories is null || request.Categories.Count == 0)
        {
            throw new InvalidDataException("未选择任何导出内容。");
        }

        var known = new HashSet<string>(CategoryDefinitions.Select(definition => definition.Id), StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.Categories)
        {
            if (!known.Contains(id))
            {
                throw new InvalidDataException($"未知的导出类别：{id}");
            }

            selected.Add(id);
        }

        return selected;
    }

    private ScanResult Scan(
        GameInstance instance,
        string root,
        CancellationToken cancellationToken,
        IProgress<InstanceExportProgress>? progress = null)
    {
        var files = new List<ExportFile>();
        var skipped = new List<ExportSkippedFile>();
        var excluded = 0;
        var complete = true;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new ExportSkippedFile(
                    Path.GetRelativePath(root, directory).Replace('\\', '/'),
                    ExportSkipReason.Unreadable,
                    exception.Message));
                complete = false;
                continue;
            }

            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= MaxFiles)
                {
                    complete = false;
                    skipped.Add(new ExportSkippedFile(
                        string.Empty,
                        ExportSkipReason.TooManyFiles,
                        $"超过 {MaxFiles} 个文件上限。"));
                    pending.Clear();
                    break;
                }

                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // 链接可能指向实例目录之外；导出不跟随链接。
                        excluded++;
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(path);
                        continue;
                    }

                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (IsEngineMetadata(relative))
                    {
                        // 身份标记与引擎缓存由 instance/ 中的元数据副本表达，不重复打包进 minecraft/。
                        continue;
                    }

                    if (IsAlwaysExcluded(relative))
                    {
                        excluded++;
                        continue;
                    }

                    var info = new FileInfo(path);
                    var category = Categorize(relative);
                    files.Add(new ExportFile(path, relative, info.Length, category));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    skipped.Add(new ExportSkippedFile(relative, ExportSkipReason.Unreadable, exception.Message));
                    complete = false;
                }
            }

            if (files.Count > 0 && files.Count % 512 == 0)
            {
                progress?.Report(new InstanceExportProgress(
                    "export-scan",
                    files.Count,
                    0,
                    files.Sum(file => file.Size),
                    0));
            }
        }

        return new ScanResult(files, skipped, excluded, complete);
    }

    private static bool IsEngineMetadata(string relativePath) =>
        string.Equals(relativePath, ".ecl", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(".ecl/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlwaysExcluded(string relativePath)
    {
        if (string.Equals(relativePath, ExportsFolderName, StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(ExportsFolderName + "/", StringComparison.OrdinalIgnoreCase))
        {
            // 导出包自身的目录永远不进入导出包，避免递归与体积膨胀。
            return true;
        }

        var fileName = Path.GetFileName(relativePath);
        if (ExcludedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExcludedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".part", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Categorize(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        foreach (var definition in CategoryDefinitions)
        {
            if (definition.Matches(normalized))
            {
                return definition.Id;
            }
        }

        return "other";
    }

    private static InstanceExportPlan BuildPlan(ScanResult scan)
    {
        var categories = CategoryDefinitions
            .Select(definition =>
            {
                var files = scan.Files.Where(file => string.Equals(file.CategoryId, definition.Id, StringComparison.Ordinal)).ToArray();
                return new ExportCategorySummary(
                    definition.Id,
                    files.Length,
                    files.Sum(file => file.Size),
                    scan.Skipped.Count(item =>
                        item.RelativePath.Length > 0 &&
                        string.Equals(Categorize(item.RelativePath), definition.Id, StringComparison.Ordinal)),
                    scan.Complete);
            })
            .ToArray();

        var warning = scan.Complete ? null : "导出扫描不完整；部分文件无法读取或超过数量上限。";
        return new InstanceExportPlan(
            categories,
            scan.Files.Count,
            scan.Files.Sum(file => file.Size),
            scan.ExcludedFileCount,
            scan.Complete,
            warning);
    }

    private async Task<Result<(GameInstance Instance, string Root)>> OpenContextAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<(GameInstance, string)>.Fail("instance.not_found", "实例不存在。");
        }

        var root = InstancePaths.GetGameDirectory(_paths, instance);
        if (!Directory.Exists(root))
        {
            return Result<(GameInstance, string)>.Fail(
                "export.root_missing",
                "实例游戏目录不存在或当前不可访问。");
        }

        return Result<(GameInstance, string)>.Ok((instance, root));
    }

    private static string BuildOutputPath(GameInstance instance, string root, bool diagnostics)
    {
        var directory = Path.Combine(root, ExportsFolderName);
        Directory.CreateDirectory(directory);
        var safeName = SanitizeFileName(instance.Name);
        var prefix = diagnostics ? "ecl-diagnostics" : "ecl-export";
        var baseName = $"{prefix}-{safeName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, baseName + ".zip");
        for (var index = 2; File.Exists(path); index++)
        {
            path = Path.Combine(directory, $"{baseName}-{index}.zip");
        }

        return path;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new System.Text.StringBuilder();
        foreach (var character in name)
        {
            if (character is ' ' or '.' or '-' or '_' || char.IsLetterOrDigit(character))
            {
                buffer.Append(invalid.Contains(character) ? '_' : character);
            }
            else
            {
                buffer.Append('_');
            }
        }

        var result = buffer.ToString().Trim(' ', '.', '_');
        if (result.Length == 0)
        {
            result = "instance";
        }

        return result.Length <= 40 ? result : result[..40];
    }

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
            // 失败清理不应掩盖最初的导出错误。
        }
    }

    private sealed record ExportFile(string FullPath, string RelativePath, long Size, string CategoryId);

    private sealed record ScanResult(
        List<ExportFile> Files,
        List<ExportSkippedFile> Skipped,
        int ExcludedFileCount,
        bool Complete);

    private sealed record InstanceExportCategoryDefinition(
        string Id,
        string[] RelativeRoots,
        string[] ExactFiles,
        ExportRiskKind Risk)
    {
        public bool Matches(string relativePath)
        {
            foreach (var exact in ExactFiles)
            {
                if (string.Equals(relativePath, exact.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var root in RelativeRoots)
            {
                var normalized = root.Replace('\\', '/');
                if (string.Equals(relativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return RelativeRoots.Length == 0 && ExactFiles.Length == 0;
        }
    }
}
