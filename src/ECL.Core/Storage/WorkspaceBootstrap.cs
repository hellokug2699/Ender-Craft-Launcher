// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>
/// 启动早期的工作区装配：在 DI 容器建立前把已确认的工作区应用到 <see cref="AppPaths"/>。
/// 仅做读取与一次性的旧配置迁移，不创建目录、不校验磁盘可用性。
/// </summary>
public static class WorkspaceBootstrap
{
    /// <summary>
    /// 应用已保存的工作区。返回加载/迁移后的状态。
    /// <paramref name="legacyGameRoot"/> 是旧版 <c>launcher.minecraftDirectory</c> 的值：
    /// 只有它是显式设置且与数据目录不同的时候，才迁移为主工作区并视为用户已确认。
    /// </summary>
    public static WorkspaceFile Apply(AppPaths paths, string? legacyGameRoot)
    {
        var store = new WorkspaceStore(paths.ConfigDirectory);
        var state = store.Load(out _);
        var primary = FindPrimary(state);

        if (primary is not null)
        {
            paths.SetGameRoot(primary.Path);
            return state;
        }

        if (string.IsNullOrWhiteSpace(legacyGameRoot))
        {
            return state;
        }

        string legacyPath;
        try
        {
            legacyPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyGameRoot.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return state;
        }

        var dataRoot = Path.TrimEndingDirectorySeparator(paths.Root);
        if (string.Equals(legacyPath, dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            // 旧默认值等于数据目录：不自动沿用为工作区，交给首次启动引导显式确认。
            return state;
        }

        var root = new WorkspaceRoot(
            WorkspaceService.CreateId(legacyPath),
            WorkspaceService.CreateName(legacyPath),
            legacyPath,
            WorkspaceKind.Managed,
            WorkspaceStatus.Unknown);

        var migrated = state with
        {
            PrimaryId = root.Id,
            Roots = [root],
            OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            store.Save(migrated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 迁移失败不能阻止启动；下一次启动会重新尝试。
            return state;
        }

        paths.SetGameRoot(root.Path);
        return migrated;
    }

    private static WorkspaceRoot? FindPrimary(WorkspaceFile state) => state.PrimaryId is null
        ? null
        : state.Roots.FirstOrDefault(root => string.Equals(root.Id, state.PrimaryId, StringComparison.Ordinal));
}
