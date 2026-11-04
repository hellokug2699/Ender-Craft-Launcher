// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>
/// 工作区路径校验。规则与 ADR 0018 对齐：
/// 拒绝系统目录、ProgramData、程序与用户数据目录；新建时可创建目录；
/// 已登记目录缺失时只报告 Missing，绝不擅自创建空目录冒充原工作区。
/// </summary>
public sealed class WorkspacePathValidator
{
    /// <summary>低于该剩余空间时给出提示。只是提示，不构成未来容量的保证。</summary>
    public const long RecommendedFreeBytes = 5L * 1024 * 1024 * 1024;

    private const string ProbeFileName = ".ecl-workspace-probe";

    private readonly string _userDataRoot;
    private readonly string[] _protectedRoots;
    private readonly string[] _appDataRoots;

    public WorkspacePathValidator(string userDataRoot)
    {
        _userDataRoot = NormalizePath(userDataRoot) ?? string.Empty;

        var roots = new List<string>();
        AddIfPresent(roots, _userDataRoot);
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        _protectedRoots = roots
            .Select(path => NormalizePath(path))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // AppData 中的位置可以显式选择，但必须提示：它默认隐藏、可能在配置文件重置时丢失，
        // 因此推荐逻辑永远不会主动给出 AppData 路径。
        _appDataRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            }
            .Select(path => NormalizePath(path))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public WorkspacePathCheck Check(string? path, WorkspaceCheckMode mode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return WorkspacePathCheck.Invalid(string.Empty, "workspace.path.empty", "工作区路径不能为空。");
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathRooted(expanded))
        {
            return WorkspacePathCheck.Invalid(expanded, "workspace.path.relative", "工作区路径必须是绝对路径。");
        }

        string fullPath;
        try
        {
            fullPath = NormalizePath(expanded)!;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return WorkspacePathCheck.Invalid(expanded, "workspace.path.invalid", $"工作区路径无效：{exception.Message}");
        }

        foreach (var protectedRoot in _protectedRoots)
        {
            if (IsUnder(fullPath, protectedRoot))
            {
                return WorkspacePathCheck.Invalid(
                    fullPath,
                    "workspace.path.protected",
                    "工作区不能位于程序目录、系统目录或 ECL 用户数据目录中。请选择用户可见的数据盘目录。");
            }
        }

        var warnings = new List<string>();
        foreach (var appDataRoot in _appDataRoots)
        {
            if (IsUnder(fullPath, appDataRoot))
            {
                warnings.Add("workspace.warning.appData");
                break;
            }
        }

        var isNetwork = fullPath.StartsWith(@"\\", StringComparison.Ordinal);
        var isDriveRoot = string.Equals(
            fullPath,
            Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
        if (isDriveRoot)
        {
            warnings.Add("workspace.warning.driveRoot");
        }

        if (isNetwork)
        {
            warnings.Add("workspace.warning.network");
        }

        long? freeBytes = null;
        if (!isNetwork)
        {
            var drive = TryGetDrive(fullPath);
            if (drive is null)
            {
                return WorkspacePathCheck.Invalid(fullPath, "workspace.path.driveMissing", "目标磁盘不存在或当前不可用。");
            }

            if (drive.DriveType == DriveType.Removable)
            {
                warnings.Add("workspace.warning.removable");
            }

            if (drive.DriveType == DriveType.Network)
            {
                warnings.Add("workspace.warning.network");
            }

            if (IsSystemDrive(drive))
            {
                warnings.Add("workspace.warning.systemDrive");
            }

            try
            {
                freeBytes = drive.AvailableFreeSpace;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                freeBytes = null;
            }
        }

        if (freeBytes is { } available && available < RecommendedFreeBytes)
        {
            warnings.Add("workspace.warning.lowSpace");
        }

        if (Directory.Exists(fullPath))
        {
            var writable = ProbeWrite(fullPath, out var writeError);
            return writable
                ? new WorkspacePathCheck(fullPath, true, WorkspaceStatus.Ready, null, null, freeBytes, warnings)
                : new WorkspacePathCheck(
                    fullPath,
                    false,
                    WorkspaceStatus.NotWritable,
                    "workspace.path.notWritable",
                    writeError ?? "工作区目录不可写。",
                    freeBytes,
                    warnings);
        }

        if (File.Exists(fullPath))
        {
            return WorkspacePathCheck.Invalid(fullPath, "workspace.path.notDirectory", "该路径已经是一个文件，不能作为工作区。");
        }

        if (mode == WorkspaceCheckMode.RequireExisting)
        {
            // 已登记工作区缺失：保留登记，提示重新连接或重新定位，绝不创建空目录。
            return new WorkspacePathCheck(
                fullPath,
                false,
                WorkspaceStatus.Missing,
                "workspace.path.missing",
                "工作区目录不存在。请重新连接磁盘或选择新的工作区位置。",
                freeBytes,
                warnings);
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new WorkspacePathCheck(
                fullPath,
                false,
                WorkspaceStatus.NotWritable,
                "workspace.path.createFailed",
                $"无法创建工作区目录：{exception.Message}",
                freeBytes,
                warnings);
        }

        var createdWritable = ProbeWrite(fullPath, out var createdError);
        return createdWritable
            ? new WorkspacePathCheck(fullPath, true, WorkspaceStatus.Ready, null, null, freeBytes, warnings)
            : new WorkspacePathCheck(
                fullPath,
                false,
                WorkspaceStatus.NotWritable,
                "workspace.path.notWritable",
                createdError ?? "工作区目录不可写。",
                freeBytes,
                warnings);
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsUnder(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        var normalizedPath = NormalizePath(path);
        var normalizedParent = NormalizePath(parent);
        if (normalizedPath is null || normalizedParent is null)
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfPresent(List<string> roots, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(path);
        }
    }

    private static DriveInfo? TryGetDrive(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSystemDrive(DriveInfo drive)
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            return string.Equals(
                Path.TrimEndingDirectorySeparator(systemRoot ?? string.Empty),
                Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return false;
        }
    }

    private static bool ProbeWrite(string directory, out string? error)
    {
        error = null;
        var probe = Path.Combine(directory, $"{ProbeFileName}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                stream.WriteByte(0x45);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"工作区目录不可写：{exception.Message}";
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响结论。
            }

            return false;
        }
    }
}
