// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Core.Workspace.Session;

/// <summary>
/// 会话快照的 JSON 持久化（<c>&lt;ConfigDirectory&gt;\workspace-session.json</c>）。
///
/// 原子写：先写同目录临时文件再替换，避免进程在写一半时被结束留下半个 JSON。
/// 读取的失败语义是“没有可恢复的会话”，不是错误：文件缺失、损坏、schema 不兼容
/// 都返回 null 并记录状态，启动流程据此决定是否询问，绝不因为快照问题阻断启动。
/// </summary>
public sealed class JsonWorkspaceSessionStore : IWorkspaceSessionStore
{
    public const string FileName = "workspace-session.json";

    private readonly IAppPaths _paths;
    private readonly ILogger<JsonWorkspaceSessionStore> _logger;

    // 用 int 保存枚举，保证跨线程读取不会读到撕裂值。
    private int _lastLoadStatus = (int)WorkspaceSessionLoadStatus.None;

    public JsonWorkspaceSessionStore(IAppPaths paths, ILogger<JsonWorkspaceSessionStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string FilePath => Path.Combine(_paths.ConfigDirectory, FileName);

    public WorkspaceSessionLoadStatus LastLoadStatus =>
        (WorkspaceSessionLoadStatus)Volatile.Read(ref _lastLoadStatus);

    public WorkspaceSessionSnapshot? Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            SetStatus(WorkspaceSessionLoadStatus.None);
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var snapshot = JsonSerializer.Deserialize<WorkspaceSessionSnapshot>(stream, EclJson.Default);
            if (snapshot is null || snapshot.SchemaVersion != WorkspaceSessionSnapshot.CurrentSchemaVersion)
            {
                // 版本不兼容时不猜测字段含义：按“不可恢复”处理，用户仍然可以手动打开工作区。
                _logger.LogInformation(
                    "工作区会话快照版本不受支持（{Version}），已忽略。",
                    snapshot?.SchemaVersion);
                SetStatus(WorkspaceSessionLoadStatus.Incompatible);
                return null;
            }

            SetStatus(WorkspaceSessionLoadStatus.Available);
            return snapshot;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "工作区会话快照损坏，已忽略：{Path}", path);
            SetStatus(WorkspaceSessionLoadStatus.Unreadable);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法读取工作区会话快照：{Path}", path);
            SetStatus(WorkspaceSessionLoadStatus.Unreadable);
            return null;
        }
    }

    public void Save(WorkspaceSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = FilePath;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, EclJson.Indented));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 会话快照是便利数据：写不进去只影响下次启动的“是否询问”，不应阻断退出。
            _logger.LogWarning(exception, "写入工作区会话快照失败：{Path}", path);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    public void Clear()
    {
        var path = FilePath;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "删除工作区会话快照失败：{Path}", path);
        }

        SetStatus(WorkspaceSessionLoadStatus.None);
    }

    private void TryDeleteTemporary(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "清理会话快照临时文件失败：{Path}", temporary);
        }
    }

    private void SetStatus(WorkspaceSessionLoadStatus status) =>
        Volatile.Write(ref _lastLoadStatus, (int)status);
}
