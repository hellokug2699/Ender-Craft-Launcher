// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Mods;

/// <summary>缺失依赖下载的进度。阶段：scan / resolve / install / done。</summary>
public sealed record ModDependencyProgress(
    string Stage,
    string Message,
    int Completed,
    int Total,
    string? CurrentFile);

public sealed record ModDependencyInstallReport(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 扫描实例中“已启用但缺少强制依赖”的 Mod，交给 <see cref="IDependencyResolver"/>
/// 解析为稳定的依赖图，再用 <see cref="IContentInstallService"/> 安装已解析的文件。
/// 无法精确解析的依赖只报告，不做模糊安装。
/// </summary>
public interface IModDependencyInstaller
{
    Task<Result<ModDependencyInstallReport>> InstallMissingAsync(
        string instanceId,
        IProgress<ModDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ModDependencyInstaller(
    IInstanceService instances,
    IModService mods,
    IDependencyResolver dependencyResolver,
    IContentInstallService contentInstall,
    ILocalizer localizer,
    ILogger<ModDependencyInstaller> logger,
    IOperationService? operations = null) : IModDependencyInstaller
{
    public async Task<Result<ModDependencyInstallReport>> InstallMissingAsync(
        string instanceId,
        IProgress<ModDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModDependencyInstallReport>.Fail("instance.not_found", "实例不存在。");
        }

        using var operation = new OperationProgressBridge<ModDependencyProgress>(
            operations,
            new OperationStart
            {
                Kind = OperationKind.Install,
                Title = localizer.Get("mods.dependencies.taskTitle", instance.Name),
                StageId = "mods.dependencies",
                Source = "Modrinth / CurseForge",
                CanCancel = true,
                CanRetry = true,
            },
            progress,
            MapProgress);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellation.Token;
        operation.AttachControl(
            cancellation,
            retryToken => InstallMissingAsync(instanceId, progress, retryToken));

        try
        {
            var result = await InstallCoreAsync(instance, operation, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                operation.Complete();
            }
            else
            {
                operation.Fail(OperationErrors.FromCode(result.Error!.Code, result.Error.Message, result.Error.Detail));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operation.Fail(OperationErrors.FromException("mods.dependencies.failed", exception));
            throw;
        }
    }

    private async Task<Result<ModDependencyInstallReport>> InstallCoreAsync(
        GameInstance instance,
        IProgress<ModDependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModDependencyProgress(
            "scan",
            localizer.Get("mods.dependencies.scanning"),
            0,
            0,
            null));

        var scan = await mods.ScanAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            return Result<ModDependencyInstallReport>.Fail(scan.Error!);
        }

        var missing = scan.Value!.Issues
            .Where(issue => issue.Kind == ModIssueKind.MissingDependency)
            .Select(issue => issue.RelatedModId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length == 0)
        {
            progress?.Report(new ModDependencyProgress(
                "done",
                localizer.Get("mods.dependencies.none"),
                0,
                0,
                null));
            return Result<ModDependencyInstallReport>.Ok(new ModDependencyInstallReport([], [], [], []));
        }

        var installed = new List<string>();
        var unresolved = new List<string>();
        var failed = new List<string>();
        var warnings = new List<string>();

        // Resolver 先产出稳定依赖图（来源/项目/版本/文件），安装阶段不再按 mod id 二次解析。
        var resolveProgress = new InlineProgress<DependencyResolutionProgress>(update =>
            progress?.Report(new ModDependencyProgress(
                "resolve",
                localizer.Get("mods.dependencies.resolving", update.CurrentDependency ?? string.Empty),
                update.Completed,
                update.Total,
                update.CurrentDependency)));
        var graph = await dependencyResolver
            .ResolveManyAsync(
                missing,
                new DependencyResolutionContext(instance.GameVersion, instance.Loader),
                resolveProgress,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var dependency in graph.Unresolved)
        {
            unresolved.Add(dependency.RequestedId);
            warnings.Add(DescribeResolution(dependency));
        }

        var resolvedDependencies = graph.Resolved;
        for (var index = 0; index < resolvedDependencies.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependency = resolvedDependencies[index];

            progress?.Report(new ModDependencyProgress(
                "install",
                localizer.Get("mods.dependencies.installing", dependency.DisplayName),
                index,
                resolvedDependencies.Count,
                dependency.DisplayName));

            var install = await contentInstall
                .InstallResolvedAsync(instance.Id, dependency, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (install.IsSuccess)
            {
                installed.Add(dependency.RequestedId);
            }
            else
            {
                failed.Add(dependency.RequestedId);
                warnings.Add($"{dependency.RequestedId}: {install.Error?.Message}");
                logger.LogWarning(
                    "安装依赖 {Dependency} 到实例 {InstanceId} 失败：{Error}",
                    dependency.RequestedId,
                    instance.Id,
                    install.Error);
            }
        }

        progress?.Report(new ModDependencyProgress(
            "done",
            localizer.Get("mods.dependencies.done", installed.Count, unresolved.Count, failed.Count),
            missing.Length,
            missing.Length,
            null));

        return Result<ModDependencyInstallReport>.Ok(
            new ModDependencyInstallReport(installed, unresolved, failed, warnings));
    }

    private string DescribeResolution(ResolvedDependency dependency) => dependency.Status switch
    {
        DependencyResolutionStatus.Ambiguous =>
            localizer.Get("mods.dependencies.ambiguous", dependency.RequestedId, dependency.Reason ?? string.Empty),
        DependencyResolutionStatus.Incompatible =>
            localizer.Get("mods.dependencies.incompatible", dependency.RequestedId, dependency.Reason ?? string.Empty),
        DependencyResolutionStatus.Failed =>
            localizer.Get("mods.dependencies.resolveFailed", dependency.RequestedId, dependency.Reason ?? string.Empty),
        _ => localizer.Get("mods.dependencies.unresolved", dependency.RequestedId),
    };

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static OperationProgressUpdate MapProgress(ModDependencyProgress progress) => new()
    {
        StageId = $"mods.dependencies.{progress.Stage}",
        StageText = progress.Message,
        CurrentItem = progress.CurrentFile,
        Completed = progress.Completed,
        Total = progress.Total > 0 ? progress.Total : null,
    };
}
