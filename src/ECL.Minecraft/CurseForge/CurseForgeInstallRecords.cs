// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.CurseForge;

/// <summary>
/// 可证明来源的 CurseForge 已装记录（F-06 §6）。记录只来自真实安装/更新计划，
/// 不从文件名或目录内容推断来源；更新检查只信任这里的稳定 ProjectId/FileId。
/// </summary>
public sealed record CurseForgeInstallRecord(
    int ProjectId,
    int FileId,
    string FileName,
    DateTimeOffset? InstalledAt = null);

/// <summary>
/// <c>.ecl-curseforge.json</c> 的唯一读写入口。安装、批量更新与更新检查共用同一格式，
/// 避免多处各自解析/覆盖同一文件。写入使用临时文件 + 原子替换；读取损坏时返回空记录而不阻断任务。
/// </summary>
public interface ICurseForgeInstallRecordStore
{
    /// <summary>读取实例的已装记录；文件缺失/损坏返回空记录，不抛异常。</summary>
    IReadOnlyList<CurseForgeInstallRecord> Load(GameInstance instance);

    /// <summary>
    /// 读取记录并区分“没有记录”和“记录损坏”：更新检查需要后者提示“结果不完整”，不得显示为已是最新。
    /// </summary>
    Result<IReadOnlyList<CurseForgeInstallRecord>> LoadWithStatus(GameInstance instance);

    /// <summary>按 ProjectId 覆盖写入记录（同一项目只保留一条），原子替换文件。</summary>
    Result Apply(GameInstance instance, IReadOnlyList<CurseForgeInstallRecord> records);

    /// <summary>移除某个项目的记录（例如用户手动删除对应内容后显式清理）。</summary>
    Result Remove(GameInstance instance, int projectId);
}

/// <summary>默认实现：实例游戏目录下的 <c>.ecl-curseforge.json</c>，JSON camelCase，兼容旧记录。</summary>
public sealed class CurseForgeInstallRecordStore(IAppPaths paths) : ICurseForgeInstallRecordStore
{
    public const string FileName = ".ecl-curseforge.json";

    private static readonly JsonSerializerOptions JsonOptions = EclJson.Indented;

    // 同一进程内串行化读改写，避免并行更新互相覆盖；跨进程写入仍是已知限制（见交接）。
    private static readonly object SaveLock = new();

    public IReadOnlyList<CurseForgeInstallRecord> Load(GameInstance instance) =>
        LoadWithStatus(instance).Value ?? [];

    public Result<IReadOnlyList<CurseForgeInstallRecord>> LoadWithStatus(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var path = GetPath(instance);
        if (!File.Exists(path))
        {
            return Result<IReadOnlyList<CurseForgeInstallRecord>>.Ok([]);
        }

        try
        {
            var document = JsonSerializer.Deserialize<InstallStateDocument>(File.ReadAllText(path), JsonOptions);
            var records = document?.Mods?
                .Where(record => record.ProjectId > 0 && record.FileId > 0 &&
                                 !string.IsNullOrWhiteSpace(record.FileName))
                .Select(record => new CurseForgeInstallRecord(
                    record.ProjectId,
                    record.FileId,
                    record.FileName!,
                    record.InstalledAt))
                .ToArray() ?? [];
            return Result<IReadOnlyList<CurseForgeInstallRecord>>.Ok(records);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return Result<IReadOnlyList<CurseForgeInstallRecord>>.Fail(
                "curseforge.record_unreadable",
                $"CurseForge 安装记录无法读取：{exception.Message}");
        }
    }

    public Result Apply(GameInstance instance, IReadOnlyList<CurseForgeInstallRecord> records)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return Result.Ok();
        }

        lock (SaveLock)
        {
            var existing = Load(instance).ToList();
            var installedAt = DateTimeOffset.UtcNow;
            foreach (var record in records)
            {
                if (record.ProjectId <= 0 || record.FileId <= 0 || string.IsNullOrWhiteSpace(record.FileName))
                {
                    continue;
                }

                existing.RemoveAll(item => item.ProjectId == record.ProjectId);
                existing.Add(record.InstalledAt is null
                    ? record with { InstalledAt = installedAt }
                    : record);
            }

            return Save(instance, existing);
        }
    }

    public Result Remove(GameInstance instance, int projectId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (projectId <= 0)
        {
            return Result.Fail("curseforge.record_invalid", "项目 ID 无效。");
        }

        lock (SaveLock)
        {
            var existing = Load(instance).Where(record => record.ProjectId != projectId).ToList();
            return Save(instance, existing);
        }
    }

    private Result Save(GameInstance instance, IReadOnlyList<CurseForgeInstallRecord> records)
    {
        try
        {
            var path = GetPath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var document = new InstallStateDocument
            {
                Mods = records
                    .Select(record => new InstallRecordDocument
                    {
                        ProjectId = record.ProjectId,
                        FileId = record.FileId,
                        FileName = record.FileName,
                        InstalledAt = record.InstalledAt,
                    })
                    .ToList(),
            };
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                "curseforge.record_write_failed",
                $"无法写入 CurseForge 安装记录：{exception.Message}");
        }
    }

    private string GetPath(GameInstance instance) =>
        Path.Combine(InstancePaths.GetGameDirectory(paths, instance), FileName);

    private sealed class InstallStateDocument
    {
        public List<InstallRecordDocument> Mods { get; set; } = [];
    }

    private sealed class InstallRecordDocument
    {
        public int ProjectId { get; set; }

        public int FileId { get; set; }

        public string FileName { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? InstalledAt { get; set; }
    }
}
