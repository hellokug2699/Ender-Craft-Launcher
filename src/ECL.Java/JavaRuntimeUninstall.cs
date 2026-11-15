// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Java;

/// <summary>卸载阻断原因。页面按类别给出具体说明，不把不同原因合并成“无法卸载”。</summary>
public enum JavaUninstallBlockerKind
{
    /// <summary>运行时不在 ECL 的托管目录：只允许“移除”登记，不允许卸载。</summary>
    NotManaged,

    /// <summary>路径不满足安全卸载条件（不在托管目录的直接子目录、重解析点、可执行文件越界）。</summary>
    PathUnsafe,

    /// <summary>仍被某个实例的显式 Java 覆盖固定引用。</summary>
    InstancePinned,

    /// <summary>仍有活动的游戏会话在使用该运行时。</summary>
    GameRunning,

    /// <summary>无法确认是否仍被使用（引用探测失败）：保守阻止，不冒险删除。</summary>
    UsageUnknown,
}

public sealed record JavaUninstallBlocker(JavaUninstallBlockerKind Kind, string Detail);

/// <summary>卸载计划：先呈现目标与阻断原因，再决定是否执行删除。</summary>
public sealed record JavaUninstallPlan(
    JavaRuntime Runtime,
    bool IsManaged,
    string HomePath,
    IReadOnlyList<JavaUninstallBlocker> Blockers)
{
    public bool CanUninstall => Blockers.Count == 0;
}

public sealed record JavaUninstallResult(bool Succeeded, string? ErrorDetail = null)
{
    public static JavaUninstallResult Ok { get; } = new(true);

    public static JavaUninstallResult Fail(string? detail) => new(false, detail);
}

/// <summary>
/// 运行时引用探测。领域层（ECL.Java）不能引用实例与会话服务，
/// 因此由上层实现该接口，把“实例固定引用/活动游戏会话”作为阻断原因交回；
/// 文件所有权与删除仍由本层负责（UI 不判断引用，也不删除文件）。
/// </summary>
public interface IJavaRuntimeUsageProvider
{
    Task<IReadOnlyList<JavaUninstallBlocker>> FindBlockersAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default);
}

public interface IJavaRuntimeUninstallService
{
    /// <summary>只计算是否可卸载及原因，不删除任何文件。</summary>
    Task<JavaUninstallPlan> CreatePlanAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default);

    /// <summary>重新校验所有权与引用后删除 ECL 托管运行时；失败时返回真实原因，不伪装成功。</summary>
    Task<JavaUninstallResult> UninstallAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default);
}

public sealed class JavaRuntimeUninstallService(
    IAppPaths paths,
    IEnumerable<IJavaRuntimeUsageProvider> usageProviders,
    ILogger<JavaRuntimeUninstallService> logger) : IJavaRuntimeUninstallService
{
    private readonly IReadOnlyList<IJavaRuntimeUsageProvider> _usageProviders =
        usageProviders?.ToArray() ?? [];

    public async Task<JavaUninstallPlan> CreatePlanAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var blockers = new List<JavaUninstallBlocker>();
        var isManaged = JavaRuntimePath.IsDirectChild(runtime.HomePath, paths.RuntimesDirectory);
        if (!isManaged)
        {
            blockers.Add(new JavaUninstallBlocker(
                JavaUninstallBlockerKind.NotManaged,
                paths.RuntimesDirectory));
        }
        else if (!JavaRuntimePath.IsSafeManagedHome(runtime, paths.RuntimesDirectory))
        {
            blockers.Add(new JavaUninstallBlocker(
                JavaUninstallBlockerKind.PathUnsafe,
                runtime.HomePath));
        }

        foreach (var provider in _usageProviders)
        {
            try
            {
                var usageBlockers = await provider
                    .FindBlockersAsync(runtime, cancellationToken)
                    .ConfigureAwait(false);
                blockers.AddRange(usageBlockers);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 无法确认引用关系时不得删除：把失败当作阻断原因，而不是当作“没有引用”。
                logger.LogWarning(exception, "无法确认 Java 运行时引用：{Path}", runtime.ExecutablePath);
                blockers.Add(new JavaUninstallBlocker(
                    JavaUninstallBlockerKind.UsageUnknown,
                    exception.Message));
            }
        }

        return new JavaUninstallPlan(runtime, isManaged, runtime.HomePath, blockers);
    }

    public async Task<JavaUninstallResult> UninstallAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        var plan = await CreatePlanAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!plan.CanUninstall)
        {
            return JavaUninstallResult.Fail(
                string.Join(" | ", plan.Blockers.Select(blocker => $"{blocker.Kind}:{blocker.Detail}")));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 删除前最后复核一次所有权边界：计划生成与删除之间仍可能发生变化。
        if (!JavaRuntimePath.IsSafeManagedHome(runtime, paths.RuntimesDirectory))
        {
            return JavaUninstallResult.Fail(plan.HomePath);
        }

        try
        {
            Directory.Delete(plan.HomePath, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(exception, "卸载 Java 运行时失败：{Path}", plan.HomePath);
            return JavaUninstallResult.Fail(exception.Message);
        }

        if (Directory.Exists(plan.HomePath))
        {
            // 部分文件被占用时 Windows 可能保留目录；如实报告，不宣称卸载完成。
            return JavaUninstallResult.Fail(plan.HomePath);
        }

        logger.LogInformation("已卸载 Java 运行时：{Path}", plan.HomePath);
        return JavaUninstallResult.Ok;
    }
}
