// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>
/// 首次启动的工作区推荐位置。顺序与 ADR 0018 一致：
/// 优先用户可见的非系统固定磁盘；没有合适磁盘时回退到文档目录下的 Games\ECL。
/// 推荐只是建议，必须由用户确认，绝不静默创建目录或开始下载。
/// </summary>
public static class WorkspaceRecommender
{
    private const string GamesFolderName = "Games";
    private const string WorkspaceFolderName = "ECL";
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    public static IReadOnlyList<WorkspaceSuggestion> Suggest(IAppPaths paths)
    {
        var suggestions = new List<WorkspaceSuggestion>();
        var systemRoot = NormalizeRoot(Path.GetPathRoot(Environment.SystemDirectory));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in EnumerateDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            var root = NormalizeRoot(drive.RootDirectory.FullName);
            if (root is null || string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Unix 的根挂载点不适合作为游戏目录建议。
            if (root.Length <= 1)
            {
                continue;
            }

            long freeBytes;
            try
            {
                freeBytes = drive.AvailableFreeSpace;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (freeBytes < MinimumFreeBytes)
            {
                continue;
            }

            var candidate = Path.Combine(root, GamesFolderName, WorkspaceFolderName);
            if (seen.Add(candidate))
            {
                suggestions.Add(new WorkspaceSuggestion(candidate, "workspace.reason.fixedDrive", suggestions.Count == 0));
            }
        }

        // 只有在没有合适的非系统固定磁盘时，才把文档目录作为可见回退建议。
        // 回退同样只是建议，必须由用户确认，绝不静默采用。
        if (suggestions.Count == 0)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                var candidate = Path.Combine(documents, GamesFolderName, WorkspaceFolderName);
                if (seen.Add(candidate))
                {
                    suggestions.Add(new WorkspaceSuggestion(candidate, "workspace.reason.documentsFallback", true));
                }
            }
        }

        // 极端情况下（无文档目录）返回空列表，由 UI 要求用户显式选择，
        // 而不是给出用户数据目录等不合适的位置。
        return suggestions;
    }

    private static IEnumerable<DriveInfo> EnumerateDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return drives;
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(root);
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
