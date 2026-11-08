// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>清单中的一行：运行时 + 它在 ECL 管理列表里的来源。</summary>
public sealed record JavaRuntimeCatalogEntry(JavaRuntime Runtime, JavaRuntimeOrigin Origin);

/// <summary>
/// Java 管理列表的组合规则（页面规格 §3.2/§5.1/§6）：
/// - 发现结果与用户登记合并，同一规范化可执行文件路径只出现一次；
/// - 用户明确移除的路径不再回到列表；
/// - 顺序 = 用户优先级（<c>java.priority</c>）在前，其余按发现默认顺序；
/// - 位于 <c>Runtimes</c> 目录的运行时标记为 ECL 托管（可卸载）。
/// 这里不读配置文件、不执行进程，只做确定性的纯组合，便于测试。
/// </summary>
public static class JavaRuntimeCatalog
{
    public static IReadOnlyList<JavaRuntimeCatalogEntry> Compose(
        IReadOnlyList<JavaRuntime> discovered,
        IReadOnlyList<JavaRuntime> imported,
        IReadOnlyList<string> removedPaths,
        IReadOnlyList<string> priorityPaths,
        string runtimesDirectory)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(imported);

        var removed = new HashSet<string>(
            (removedPaths ?? []).Select(path => JavaRuntimePath.Canonicalize(path)),
            StringComparer.Ordinal);
        var importedPaths = new HashSet<string>(
            imported.Select(runtime => JavaRuntimePath.Canonicalize(runtime.ExecutablePath)),
            StringComparer.Ordinal);

        var candidates = discovered
            .Concat(imported)
            .Where(runtime => runtime is not null)
            .Where(runtime => !removed.Contains(JavaRuntimePath.Canonicalize(runtime.ExecutablePath)))
            .GroupBy(runtime => JavaRuntimePath.Canonicalize(runtime.ExecutablePath), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var ordered = JavaPriorityOrdering.Order(candidates, priorityPaths ?? []);

        return ordered.Runtimes
            .Select(runtime => new JavaRuntimeCatalogEntry(
                runtime,
                ResolveOrigin(runtime, importedPaths, runtimesDirectory)))
            .ToArray();
    }

    public static JavaRuntimeOrigin ResolveOrigin(
        JavaRuntime runtime,
        IReadOnlyCollection<string> importedExecutablePaths,
        string runtimesDirectory)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!string.IsNullOrWhiteSpace(runtimesDirectory) &&
            JavaRuntimePath.IsDirectChild(runtime.HomePath, runtimesDirectory))
        {
            return JavaRuntimeOrigin.EclManaged;
        }

        var canonical = JavaRuntimePath.Canonicalize(runtime.ExecutablePath);
        return importedExecutablePaths.Contains(canonical)
            ? JavaRuntimeOrigin.UserImported
            : JavaRuntimeOrigin.Discovered;
    }
}
