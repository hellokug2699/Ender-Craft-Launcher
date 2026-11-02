// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Dimensions;

namespace Ecl.World.Worlds;

public sealed record WorldScanResult(
    IReadOnlyList<WorldSave> Worlds,
    IReadOnlyList<string> Warnings,
    bool IsAccessible)
{
    public static WorldScanResult Inaccessible(string warning) => new([], [warning], false);
}

public sealed record WorldOperationResult<T>(T? Value, string? Error)
    where T : class
{
    public bool Success => Error is null;
}

public sealed record WorldBackup(string DirectoryPath, long Bytes);

public sealed record WorldDeleteResult(bool Deleted);

/// <summary>
/// 存档发现与安全管理。默认只读；备份/删除必须由调用方在用户确认后发起，
/// 且目标必须位于当前实例的 <c>saves</c> 目录内。
/// </summary>
public sealed class WorldSaveService(string gameDirectory)
{
    private readonly string _savesDirectory = Path.GetFullPath(Path.Combine(gameDirectory, "saves"));

    public string SavesDirectory => _savesDirectory;

    public Task<WorldScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (!Directory.Exists(_savesDirectory))
                {
                    return new WorldScanResult([], [], true);
                }

                var worlds = new List<WorldSave>();
                var warnings = new List<string>();
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(_savesDirectory).Take(512))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var levelDat = Path.Combine(directory, "level.dat");
                        var region = Path.Combine(directory, "region");
                        if (!File.Exists(levelDat) && !Directory.Exists(region))
                        {
                            continue;
                        }

                        var level = File.Exists(levelDat)
                            ? WorldLevelReader.Read(levelDat)
                            : new WorldLevelInfo { Error = "缺少 level.dat。" };
                        if (!level.IsReadable)
                        {
                            warnings.Add($"{Path.GetFileName(directory)}：{level.Error}");
                        }

                        var dimensions = new DimensionDiscoveryService().Discover(directory);
                        foreach (var warning in dimensions.Warnings)
                        {
                            warnings.Add($"{Path.GetFileName(directory)}：{warning}");
                        }

                        var icon = Path.Combine(directory, "icon.png");
                        worlds.Add(new WorldSave(
                            string.IsNullOrWhiteSpace(level.LevelName) ? Path.GetFileName(directory) : level.LevelName,
                            Path.GetFullPath(directory),
                            levelDat,
                            level,
                            dimensions.Dimensions.Select(DimensionProjection.ToDimensionInfo).ToArray(),
                            File.Exists(icon) ? icon : null)
                        {
                            Discovery = dimensions,
                        });
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new WorldScanResult(worlds, warnings, false);
                }

                return new WorldScanResult(
                    worlds
                        .OrderByDescending(world => world.Level.LastPlayed ?? DateTimeOffset.MinValue)
                        .ThenBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    warnings,
                    true);
            },
            cancellationToken);

    public async Task<WorldOperationResult<WorldBackup>> BackupAsync(
        WorldSave world,
        string backupRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!WorldPathSafety.TryNormalizeUnder(_savesDirectory, world.DirectoryPath, out var source))
        {
            return new WorldOperationResult<WorldBackup>(null, "存档目录不在当前实例的 saves 范围内。");
        }

        if (!Directory.Exists(source))
        {
            return new WorldOperationResult<WorldBackup>(null, "存档目录不存在。");
        }

        if (IsReparsePoint(source))
        {
            return new WorldOperationResult<WorldBackup>(null, "存档目录是重解析点，拒绝执行备份。");
        }

        string root;
        try
        {
            root = Path.GetFullPath(backupRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new WorldOperationResult<WorldBackup>(null, $"备份目录无效：{exception.Message}");
        }

        if (WorldPathSafety.IsContainedIn(source, root))
        {
            return new WorldOperationResult<WorldBackup>(null, "备份目录不能位于存档目录内部。");
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var destination = Path.Combine(root, $"{Path.GetFileName(source)}-{stamp}");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(root);
            if (Directory.Exists(destination))
            {
                return new WorldOperationResult<WorldBackup>(null, "同名备份已存在，请稍后重试。");
            }

            var bytes = await CopyDirectoryAsync(source, temporary, progress, cancellationToken).ConfigureAwait(false);
            Directory.Move(temporary, destination);
            return new WorldOperationResult<WorldBackup>(new WorldBackup(destination, bytes), null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(temporary);
            return new WorldOperationResult<WorldBackup>(null, $"备份失败：{exception.Message}");
        }
    }

    public Task<WorldOperationResult<WorldDeleteResult>> DeleteAsync(
        WorldSave world,
        Func<WorldSave, bool>? isInUse = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (!WorldPathSafety.TryNormalizeUnder(_savesDirectory, world.DirectoryPath, out var target))
                {
                    return new WorldOperationResult<WorldDeleteResult>(null, "存档目录不在当前实例的 saves 范围内。");
                }

                if (IsReparsePoint(target))
                {
                    return new WorldOperationResult<WorldDeleteResult>(null, "存档目录是重解析点，拒绝删除。");
                }

                if (isInUse?.Invoke(world) == true)
                {
                    return new WorldOperationResult<WorldDeleteResult>(null, "存档正在被游戏或备份任务使用。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var quarantine = target + ".ecl-delete-" + Guid.NewGuid().ToString("N")[..8];
                try
                {
                    Directory.Move(target, quarantine);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new WorldOperationResult<WorldDeleteResult>(null, $"无法准备删除：{exception.Message}");
                }

                try
                {
                    Directory.Delete(quarantine, recursive: true);
                    return new WorldOperationResult<WorldDeleteResult>(new WorldDeleteResult(true), null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryMoveBack(quarantine, target);
                    return new WorldOperationResult<WorldDeleteResult>(null, $"删除未完成，已尝试恢复原目录：{exception.Message}");
                }
            },
            cancellationToken);

    private static async Task<long> CopyDirectoryAsync(
        string source,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        long copied = 0;
        var files = Directory.EnumerateFiles(
                source,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = 64,
                })
            .ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            copied += new FileInfo(file).Length;
            progress?.Report(files.Length == 0 ? 1 : (index + 1d) / files.Length);
        }

        return copied;
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

    private static void TryMoveBack(string quarantine, string target)
    {
        try
        {
            if (Directory.Exists(quarantine) && !Directory.Exists(target))
            {
                Directory.Move(quarantine, target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 保留隔离目录，结果中已说明删除未完成。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时目录残留不改变主结果。
        }
    }
}

public static class WorldPathSafety
{
    public static bool IsContainedIn(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryNormalizeUnder(string root, string candidate, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalized = fullCandidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
