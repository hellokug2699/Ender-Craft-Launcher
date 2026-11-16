// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Engine.Lifecycle;

public enum InstancePathCheckStatus
{
    Allowed,
    Warning,
    Rejected,
}

public sealed record InstancePathCheck(
    string Path,
    InstancePathCheckStatus Status,
    string? ErrorCode,
    string? Detail,
    bool IsReparsePoint,
    string? FinalPath,
    IReadOnlyList<string> Warnings)
{
    public bool IsAllowed => Status != InstancePathCheckStatus.Rejected;
}

/// <summary>
/// 实例生命周期路径策略（F-03 §5.1）。破坏性操作必须失败关闭：
/// 卷根、用户/系统/程序/ECL 目录、重解析点逃逸、保留设备名与 ADS 一律拒绝。
/// </summary>
public interface IInstancePathPolicy
{
    InstancePathCheck CheckTarget(string path, bool allowMissing);

    InstancePathCheck CheckSource(string path);

    InstancePathCheck CheckDeletionEntry(string path, string allowedRoot);

    InstancePathCheck CheckMove(string sourceDirectory, string targetDirectory);

    bool IsUnderProtectedRoot(string path);
}

public sealed class InstancePathPolicy : IInstancePathPolicy
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly IAppPaths _paths;
    private readonly IReadOnlyList<string> _protectedRoots;
    private readonly IReadOnlyList<string> _warningRoots;
    private readonly string? _userProfile;

    public InstancePathPolicy(IAppPaths paths)
    {
        _paths = paths;
        var protectedRoots = new List<string>
        {
            paths.Root,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _userProfile = Normalize(userProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.DesktopDirectory,
                         Environment.SpecialFolder.MyDocuments,
                         Environment.SpecialFolder.MyPictures,
                         Environment.SpecialFolder.MyMusic,
                         Environment.SpecialFolder.MyVideos,
                     })
            {
                var value = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    protectedRoots.Add(value);
                }
            }
        }

        _protectedRoots = protectedRoots
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Normalize(item))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _warningRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Normalize(item))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public InstancePathCheck CheckTarget(string path, bool allowMissing) =>
        CheckCore(path, allowMissing, requireExistingDirectory: false);

    public InstancePathCheck CheckSource(string path)
    {
        var check = CheckCore(path, allowMissing: false, requireExistingDirectory: true);
        return check;
    }

    public InstancePathCheck CheckDeletionEntry(string path, string allowedRoot)
    {
        var check = CheckCore(path, allowMissing: true, requireExistingDirectory: false);
        var normalized = Normalize(check.FinalPath ?? path);
        var root = Normalize(allowedRoot);
        if (normalized is not null && root is not null && IsUnder(normalized, root) &&
            check.ErrorCode == "instance.path.protected_root")
        {
            // 受保护根内的 ECL 实例数据：只要精确位于计划记录目录内，允许删除。
            check = check with
            {
                Status = check.Warnings.Count > 0 ? InstancePathCheckStatus.Warning : InstancePathCheckStatus.Allowed,
                ErrorCode = null,
                Detail = null,
            };
        }

        if (!check.IsAllowed)
        {
            return check;
        }

        if (normalized is null || root is null || !IsUnder(normalized, root))
        {
            return new InstancePathCheck(
                path,
                InstancePathCheckStatus.Rejected,
                "instance.path.owner_scope",
                "删除目标不在允许的所有权范围内。",
                check.IsReparsePoint,
                check.FinalPath,
                check.Warnings);
        }

        return check;
    }

    public InstancePathCheck CheckMove(string sourceDirectory, string targetDirectory)
    {
        var source = CheckSource(sourceDirectory);
        if (!source.IsAllowed)
        {
            return source;
        }

        var target = CheckTarget(targetDirectory, allowMissing: true);
        if (!target.IsAllowed)
        {
            return target;
        }

        var sourcePath = Normalize(source.FinalPath ?? sourceDirectory);
        var targetPath = Normalize(target.FinalPath ?? targetDirectory);
        if (sourcePath is null || targetPath is null)
        {
            return Rejected(targetDirectory, "instance.path.invalid", "源或目标路径无效。");
        }

        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            IsUnder(targetPath, sourcePath) ||
            IsUnder(sourcePath, targetPath))
        {
            return Rejected(targetDirectory, "instance.path.overlap", "源与目标路径重叠。");
        }

        return target with { Warnings = source.Warnings.Concat(target.Warnings).ToArray() };
    }

    public bool IsUnderProtectedRoot(string path)
    {
        var normalized = Normalize(path);
        return normalized is not null &&
               _protectedRoots.Any(root => IsUnder(normalized, root));
    }

    private InstancePathCheck CheckCore(string path, bool allowMissing, bool requireExistingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Rejected(path, "instance.path.empty", "路径不能为空。");
        }

        string full;
        try
        {
            full = Normalize(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())))!;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Rejected(path, "instance.path.invalid", "路径无效。", exception.Message);
        }

        var root = Path.GetPathRoot(full);
        var relativePart = string.IsNullOrWhiteSpace(root) ? full : full[root.Length..];
        if (relativePart.Contains(':'))
        {
            return Rejected(full, "instance.path.reserved_name", "路径包含备用数据流分隔符。");
        }

        foreach (var segment in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.TrimEnd('.', ' ');
            if (!string.Equals(trimmed, segment, StringComparison.Ordinal))
            {
                return Rejected(full, "instance.path.reserved_name", "路径段以点或空格结尾。");
            }

            var name = segment.Split('.')[0];
            if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return Rejected(full, "instance.path.reserved_name", $"路径段使用了保留名称：{segment}");
            }
        }

        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(full),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(full, "instance.path.volume_root", "不能把卷根作为实例目标。");
        }

        foreach (var protectedRoot in _protectedRoots)
        {
            if (IsUnder(full, protectedRoot))
            {
                return Rejected(
                    full,
                    "instance.path.protected_root",
                    "路径位于系统、程序、用户目录或 ECL 自身数据目录内。");
            }
        }

        if (_userProfile is not null && string.Equals(full, _userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(full, "instance.path.protected_root", "不能把用户主目录作为实例目标。");
        }

        if (Directory.Exists(full))
        {
            if (IsReparsePoint(full))
            {
                return Rejected(full, "instance.path.reparse_escape", "目标目录是重解析点/符号链接。");
            }

            if (HasReparsePointInChain(full))
            {
                return Rejected(full, "instance.path.reparse_escape", "目标路径包含重解析点/符号链接。");
            }
        }
        else if (!allowMissing)
        {
            return Rejected(full, "instance.path.missing", "路径不存在。");
        }
        else if (HasReparsePointInChain(Path.GetDirectoryName(full) ?? full))
        {
            return Rejected(full, "instance.path.reparse_escape", "目标路径包含重解析点/符号链接。");
        }
        else if (requireExistingDirectory)
        {
            return Rejected(full, "instance.path.missing", "路径不存在。");
        }

        var warnings = new List<string>();
        foreach (var warningRoot in _warningRoots)
        {
            if (IsUnder(full, warningRoot))
            {
                warnings.Add("instance.path.warning.appData");
                break;
            }
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            warnings.Add("instance.path.warning.network");
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType == DriveType.Removable)
            {
                warnings.Add("instance.path.warning.removable");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            warnings.Add("instance.path.warning.driveUnavailable");
        }

        return new InstancePathCheck(
            full,
            warnings.Count > 0 ? InstancePathCheckStatus.Warning : InstancePathCheckStatus.Allowed,
            null,
            null,
            Directory.Exists(full) && IsReparsePoint(full),
            full,
            warnings);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasReparsePointInChain(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = Normalize(path);
        var normalizedRoot = Normalize(root);
        if (normalizedPath is null || normalizedRoot is null)
        {
            return false;
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static InstancePathCheck Rejected(
        string path,
        string code,
        string detail,
        string? extra = null) =>
        new(
            path,
            InstancePathCheckStatus.Rejected,
            code,
            extra is null ? detail : $"{detail} {extra}",
            false,
            null,
            []);
}
