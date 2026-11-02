// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Instances;
using Ecl.World.Diagnostics;
using Ecl.World.Recent;
using Ecl.World.Rendering;
using Ecl.World.Servers;
using Ecl.World.Worlds;

namespace Ecl.Minecraft.World;

public sealed record MapView(
    string WorldName,
    string DimensionId,
    MapImage Image,
    IReadOnlyList<DimensionInfo> Dimensions,
    int CenterChunkX,
    int CenterChunkZ,
    int CorruptChunks,
    int MissingChunks)
{
    public bool IsEmpty => Image.IsEmpty;
}

public sealed record WorldBackupView(WorldBackup Backup, string Message);

/// <summary>
/// 实例工作台的世界/服务器/诊断组合服务。文件与网络操作均异步、可取消；
/// 存档删除/备份仅在调用方明确确认后执行，且限制在实例游戏目录的 saves 内。
/// </summary>
public sealed class WorldWorkbenchService(
    IInstanceService instances,
    IAppPaths paths,
    RecentAccessStore recentAccess,
    DiagnosticReportStore diagnostics,
    WorldDiagnosticsParser parser,
    ServerPingService ping)
{
    public async Task<IReadOnlyList<WorldSave>> ListWorldsAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return [];
        }

        var service = new WorldSaveService(InstancePaths.GetGameDirectory(paths, instance));
        var scan = await service.ScanAsync(cancellationToken).ConfigureAwait(false);
        return scan.Worlds;
    }

    public async Task<MapView?> RenderMapAsync(
        string instanceId,
        string worldFolder,
        string dimensionId,
        int centerChunkX,
        int centerChunkZ,
        int radius,
        MapLayer layer,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var service = new WorldSaveService(InstancePaths.GetGameDirectory(paths, instance));
        var scan = await service.ScanAsync(cancellationToken).ConfigureAwait(false);
        var world = scan.Worlds.FirstOrDefault(item =>
            string.Equals(item.FolderName, worldFolder, StringComparison.OrdinalIgnoreCase));
        if (world is null)
        {
            return null;
        }

        var dimension = world.FindDimension(dimensionId)
                        ?? world.Dimensions.FirstOrDefault(dimension => dimension.HasStorage);
        if (dimension is null)
        {
            return null;
        }

        radius = Math.Clamp(radius, 0, 16);
        using var reader = new WorldChunkReader(world, dimension);
        var chunks = await Task.Run(
                () => reader.ReadArea(centerChunkX, centerChunkZ, radius, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var events = await diagnostics.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var highlights = events
            .Where(item =>
                item.Location.HasChunk &&
                string.Equals(item.Location.DimensionId ?? dimension.Id, dimension.Id, StringComparison.Ordinal))
            .Select(item => new MapHighlight(item.Location.ChunkX!.Value, item.Location.ChunkZ!.Value, MapHighlightKind.Diagnostic))
            .Take(128)
            .ToArray();
        var image = MapRenderer.Render(chunks, layer, pixelsPerBlock: 2, highlights);
        return new MapView(
            world.Name,
            dimension.Id,
            image,
            world.Dimensions,
            centerChunkX,
            centerChunkZ,
            chunks.Count(item => item.Status == Ecl.World.Region.RegionChunkStatus.Corrupt),
            chunks.Count(item => item.Status == Ecl.World.Region.RegionChunkStatus.Missing));
    }

    public async Task<WorldBackupView?> BackupWorldAsync(
        string instanceId,
        string worldFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var worlds = await ListWorldsAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var world = worlds.FirstOrDefault(item =>
            string.Equals(item.FolderName, worldFolder, StringComparison.OrdinalIgnoreCase));
        if (world is null)
        {
            return null;
        }

        var service = new WorldSaveService(InstancePaths.GetGameDirectory(paths, instance));
        var result = await service.BackupAsync(
                world,
                Path.Combine(paths.Root, "backups", "worlds", instanceId),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return new WorldBackupView(new WorldBackup(string.Empty, 0), result.Error ?? "备份失败。");
        }

        await RecordWorldVisitAsync(instanceId, world, cancellationToken).ConfigureAwait(false);
        return new WorldBackupView(result.Value!, $"已备份到 {result.Value!.DirectoryPath}");
    }

    public async Task<string?> DeleteWorldAsync(
        string instanceId,
        string worldFolder,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return "删除存档需要额外确认。";
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return "实例不存在。";
        }

        var worlds = await ListWorldsAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var world = worlds.FirstOrDefault(item =>
            string.Equals(item.FolderName, worldFolder, StringComparison.OrdinalIgnoreCase));
        if (world is null)
        {
            return "存档不存在。";
        }

        var service = new WorldSaveService(InstancePaths.GetGameDirectory(paths, instance));
        var result = await service.DeleteAsync(world, save => IsWorldInUse(save), cancellationToken).ConfigureAwait(false);
        return result.Success ? null : result.Error;
    }

    public async Task<ServerListResult> ListServersAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new ServerListResult([], "实例不存在。");
        }

        var service = new ServerListService(InstancePaths.GetGameDirectory(paths, instance));
        return await service.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServerPingResult>> PingServersAsync(
        string instanceId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var list = await ListServersAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!list.IsReadable)
        {
            return [];
        }

        var results = await ping.QueryManyAsync(list.Servers, refresh, cancellationToken).ConfigureAwait(false);
        foreach (var result in results.Where(item => item.Online))
        {
            var entry = list.Servers.FirstOrDefault(server =>
                string.Equals(server.Host, result.Host, StringComparison.OrdinalIgnoreCase) && server.Port == result.Port);
            if (entry is not null)
            {
                await recentAccess.RecordAsync(
                        new RecentAccessEntry
                        {
                            Kind = RecentAccessKind.Server,
                            InstanceId = instanceId,
                            TargetId = entry.Address,
                            DisplayName = entry.Name,
                            Detail = result.PlayersOnline is { } online && result.PlayersMax is { } max
                                ? $"{online}/{max}"
                                : result.StatusText,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return results;
    }

    public async Task<DiagnosticAnalysisResult> AnalyzeDiagnosticsAsync(
        string instanceId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return new DiagnosticAnalysisResult([], 0, false, "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
        var files = new List<string>();
        AddLogs(paths.GetInstanceLogsDirectory(instance.Id), files);
        AddLogs(Path.Combine(gameDirectory, "logs"), files);
        AddCrashReports(Path.Combine(gameDirectory, "crash-reports"), files);
        var result = await parser.AnalyzeAsync(instanceId, sessionId, files, cancellationToken).ConfigureAwait(false);
        if (result.Events.Count > 0)
        {
            await diagnostics.MergeAsync(instanceId, result.Events, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public Task<IReadOnlyList<DiagnosticEvent>> LoadDiagnosticsAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        diagnostics.LoadAsync(instanceId, cancellationToken);

    public async Task RecordWorldVisitAsync(
        string instanceId,
        WorldSave world,
        CancellationToken cancellationToken = default)
    {
        await recentAccess.RecordAsync(
                new RecentAccessEntry
                {
                    Kind = RecentAccessKind.World,
                    InstanceId = instanceId,
                    TargetId = world.FolderName,
                    DisplayName = world.Name,
                    Detail = world.Level.VersionName,
                    WorldPath = world.DirectoryPath,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentAccessEntry>> RecentAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await recentAccess.LoadAsync(cancellationToken).ConfigureAwait(false);
        return instanceId is null
            ? snapshot.Entries
            : snapshot.Entries.Where(entry => string.Equals(entry.InstanceId, instanceId, StringComparison.Ordinal)).ToArray();
    }

    public async Task ClearRecentAsync(CancellationToken cancellationToken = default) =>
        await recentAccess.ClearAsync(cancellationToken).ConfigureAwait(false);

    public async Task RemoveRecentAsync(string id, CancellationToken cancellationToken = default) =>
        await recentAccess.RemoveAsync(id, cancellationToken).ConfigureAwait(false);

    private static void AddLogs(string directory, List<string> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly).Take(8));
            files.AddRange(Directory.EnumerateFiles(directory, "*.log.gz", SearchOption.TopDirectoryOnly).Take(8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 日志目录不可读时保留其他来源。
        }
    }

    private static void AddCrashReports(string directory, List<string> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).Take(8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 崩溃报告目录不可读时保留其他来源。
        }
    }

    private static bool IsWorldInUse(WorldSave world)
    {
        try
        {
            var lockFile = Path.Combine(world.DirectoryPath, "session.lock");
            if (!File.Exists(lockFile))
            {
                return false;
            }

            using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
