// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Mods;

public enum ContentPackKind
{
    ResourcePack,
    ShaderPack,
}

/// <summary>options.txt 中资源包/光影包配置的读取状态。</summary>
public enum PackOptionsStatus
{
    /// <summary>实例还没有 options.txt（从未启动过游戏）。</summary>
    NotInitialized,

    /// <summary>配置可读。</summary>
    Valid,

    /// <summary>配置存在但无法解析；本模块不会覆盖该文件。</summary>
    Unreadable,
}

/// <summary>磁盘上的一个资源包/光影包文件，以及它在 options.txt 中的启用状态。</summary>
public sealed record ContentPackEntry(
    string FileName,
    string FullPath,
    ContentPackKind Kind,
    string Name,
    string? Description,
    int? PackFormat,
    bool Enabled,
    bool Incompatible);

/// <summary>
/// 一次资源包/光影包列表查询的结果。
/// <para>
/// <see cref="MissingEntries"/> 是 options.txt 中已启用、但磁盘上没有对应文件的条目；
/// <see cref="UnmanagedEntries"/> 是本模块不管理的条目（例如 <c>vanilla</c> 之外的旧式写法）；
/// <see cref="DuplicateEntries"/> 是 options.txt 中重复出现的条目。三者都只用于提示，
/// 不会在未发生显式写入时自动清理，避免静默丢弃用户配置。
/// </para>
/// </summary>
public sealed record ContentPackList(
    IReadOnlyList<ContentPackEntry> Packs,
    IReadOnlyList<string> MissingEntries,
    IReadOnlyList<string> UnmanagedEntries,
    IReadOnlyList<string> DuplicateEntries,
    PackOptionsStatus Status,
    string? Warning)
{
    public int EnabledCount => Packs.Count(pack => pack.Enabled);
}

public interface IContentPackService
{
    Task<Result<ContentPackList>> ListAsync(
        string instanceId,
        ContentPackKind kind,
        CancellationToken cancellationToken = default);

    Task<Result<ContentPackEntry>> ImportAsync(
        string instanceId,
        ContentPackKind kind,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改启用状态。资源包会写入 <c>resourcePacks</c> 的 <c>file/</c> 条目；
    /// 光影包会写入 <c>shaderPack</c>（同一时间只有一个）。
    /// </summary>
    Task<Result> SetEnabledAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default);
}

public sealed class ContentPackService(
    IInstanceService instances,
    IAppPaths paths) : IContentPackService
{
    private const string ResourcePackEntryPrefix = "file/";
    private const string VanillaResourcePack = "vanilla";

    public async Task<Result<ContentPackList>> ListAsync(
        string instanceId,
        ContentPackKind kind,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentPackList>.Fail("instance.not_found", "实例不存在。");
        }

        var options = GameOptionsFile.Load(GetOptionsPath(instance));
        if (!options.IsSuccess)
        {
            return Result<ContentPackList>.Fail(options.Error!);
        }

        var selection = ReadSelection(options.Value!, kind);
        var enabledEntries = selection.Enabled;
        var duplicates = enabledEntries
            .GroupBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var distinctEnabled = enabledEntries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var enabledFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmanaged = new List<string>();
        foreach (var entry in distinctEnabled)
        {
            var fileName = kind == ContentPackKind.ResourcePack
                ? ResourcePackFileName(entry)
                : entry;

            if (fileName is null)
            {
                // vanilla 是游戏默认资源包，不属于本模块管理范围，也不算未知条目。
                if (!string.Equals(entry, VanillaResourcePack, StringComparison.OrdinalIgnoreCase))
                {
                    unmanaged.Add(entry);
                }

                continue;
            }

            enabledFileNames.Add(fileName);
        }

        var directory = GetDirectory(instance, kind);
        var files = Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        var packs = files
            .Select(file => ReadEntry(
                file,
                kind,
                enabledFileNames.Contains(Path.GetFileName(file)),
                IsIncompatible(selection.Incompatible, kind, Path.GetFileName(file))))
            .ToArray();

        var existingNames = new HashSet<string>(
            files.Select(file => Path.GetFileName(file)),
            StringComparer.OrdinalIgnoreCase);
        var missing = enabledFileNames
            .Where(name => !existingNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => kind == ContentPackKind.ResourcePack ? ResourcePackEntryPrefix + name : name)
            .ToArray();

        return Result<ContentPackList>.Ok(new ContentPackList(
            packs,
            missing,
            unmanaged,
            duplicates,
            selection.Status,
            selection.Warning));
    }

    public async Task<Result<ContentPackEntry>> ImportAsync(
        string instanceId,
        ContentPackKind kind,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentPackEntry>.Fail("instance.not_found", "实例不存在。");
        }

        if (!File.Exists(sourcePath))
        {
            return Result<ContentPackEntry>.Fail("pack.not_found", $"文件不存在：{sourcePath}");
        }

        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ContentPackEntry>.Fail("pack.invalid_file", "只支持 .zip 格式的资源包/光影包。");
        }

        var directory = GetDirectory(instance, kind);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(sourcePath));
        if (File.Exists(destination) && !overwrite)
        {
            return Result<ContentPackEntry>.Fail("pack.target_exists", $"目标已存在：{Path.GetFileName(sourcePath)}");
        }

        try
        {
            File.Copy(sourcePath, destination, overwrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<ContentPackEntry>.Fail("pack.import_failed", $"导入失败：{exception.Message}");
        }

        var fileName = Path.GetFileName(destination);
        var enabled = false;
        var incompatible = false;
        var options = GameOptionsFile.Load(GetOptionsPath(instance));
        if (options.IsSuccess)
        {
            var selection = ReadSelection(options.Value!, kind);
            enabled = selection.Status == PackOptionsStatus.Valid && selection.Enabled
                .Any(entry => MatchesFile(kind, entry, fileName));
            incompatible = IsIncompatible(selection.Incompatible, kind, fileName);
        }

        return Result<ContentPackEntry>.Ok(ReadEntry(destination, kind, enabled, incompatible));
    }

    public async Task<Result> DeleteAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidFileName(fileName))
        {
            return Result.Fail("pack.invalid_file", "文件名不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        var path = Path.Combine(GetDirectory(instance, kind), fileName);
        if (!File.Exists(path))
        {
            return Result.Fail("pack.not_found", $"文件不存在：{fileName}");
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("pack.delete_failed", $"删除失败：{exception.Message}");
        }

        // 删除文件后同步清理 options.txt 中对应的启用条目，避免游戏读到失效引用。
        // 清理失败不影响文件删除，下一次 list 会把残留条目报告为“文件缺失”。
        PruneSelection(instance, kind, fileName);
        return Result.Ok();
    }

    public async Task<Result> SetEnabledAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidFileName(fileName))
        {
            return Result.Fail("pack.invalid_file", "文件名不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        var options = GameOptionsFile.Load(GetOptionsPath(instance));
        if (!options.IsSuccess)
        {
            return Result.Fail(options.Error!);
        }

        var document = options.Value!;
        var selection = ReadSelection(document, kind);
        if (selection.Status == PackOptionsStatus.Unreadable)
        {
            return Result.Fail(
                "options.invalid_value",
                selection.Warning ?? "options.txt 中的资源包配置无法解析，已保持原文件不变。",
                document.Path);
        }

        var directory = GetDirectory(instance, kind);

        if (enabled && !File.Exists(Path.Combine(directory, fileName)))
        {
            return Result.Fail("pack.not_found", $"文件不存在：{fileName}");
        }

        if (kind == ContentPackKind.ShaderPack)
        {
            SetShaderEnabled(document, fileName, enabled);
        }
        else
        {
            var result = SetResourcePackEnabled(document, selection, fileName, enabled);
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return document.IsDirty ? document.Save() : Result.Ok();
    }

    private static Result SetResourcePackEnabled(
        GameOptionsFile document,
        KindSelection selection,
        string fileName,
        bool enabled)
    {
        var entry = ResourcePackEntryPrefix + fileName;
        var current = selection.Enabled.ToList();
        var present = current.Any(candidate =>
            string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase));

        if (enabled)
        {
            if (!present)
            {
                if (document.GetValue("resourcePacks") is null)
                {
                    // 键缺失时游戏按默认值 ["vanilla"] 处理；补上默认项，避免覆盖默认行为。
                    current.Insert(0, VanillaResourcePack);
                }

                current.Add(entry);
                document.SetStringList("resourcePacks", current);
            }

            return Result.Ok();
        }

        if (!present)
        {
            return Result.Ok();
        }

        current.RemoveAll(candidate =>
            string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase));
        document.SetStringList("resourcePacks", current);

        // 禁用时同时移除“已确认不兼容”的记录，重新启用后由游戏再次提示。
        var incompatible = selection.Incompatible
            .Where(candidate => !string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (incompatible.Length != selection.Incompatible.Count)
        {
            document.SetStringList("incompatibleResourcePacks", incompatible);
        }

        return Result.Ok();
    }

    private static void SetShaderEnabled(
        GameOptionsFile document,
        string fileName,
        bool enabled)
    {
        var active = document.GetString("shaderPack");
        if (enabled)
        {
            if (!string.Equals(active, fileName, StringComparison.OrdinalIgnoreCase))
            {
                document.SetString("shaderPack", fileName);
            }

            return;
        }

        if (active is not null && string.Equals(active, fileName, StringComparison.OrdinalIgnoreCase))
        {
            document.Remove("shaderPack");
        }
    }

    private void PruneSelection(GameInstance instance, ContentPackKind kind, string fileName)
    {
        var options = GameOptionsFile.Load(GetOptionsPath(instance));
        if (!options.IsSuccess)
        {
            return;
        }

        var document = options.Value!;
        var selection = ReadSelection(document, kind);
        if (selection.Status == PackOptionsStatus.Unreadable)
        {
            return;
        }

        if (kind == ContentPackKind.ShaderPack)
        {
            var active = document.GetString("shaderPack");
            if (active is not null && string.Equals(active, fileName, StringComparison.OrdinalIgnoreCase))
            {
                document.Remove("shaderPack");
            }
        }
        else
        {
            var entry = ResourcePackEntryPrefix + fileName;
            var current = selection.Enabled
                .Where(candidate => !string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (current.Length != selection.Enabled.Count)
            {
                document.SetStringList("resourcePacks", current);
            }

            var incompatible = selection.Incompatible
                .Where(candidate => !string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (incompatible.Length != selection.Incompatible.Count)
            {
                document.SetStringList("incompatibleResourcePacks", incompatible);
            }
        }

        if (document.IsDirty)
        {
            document.Save();
        }
    }

    private static KindSelection ReadSelection(GameOptionsFile document, ContentPackKind kind)
    {
        if (!document.Exists)
        {
            return new KindSelection([], [], PackOptionsStatus.NotInitialized, null);
        }

        if (kind == ContentPackKind.ShaderPack)
        {
            var active = document.GetString("shaderPack");
            IReadOnlyList<string> enabled = string.IsNullOrWhiteSpace(active) ? [] : [active];
            return new KindSelection(enabled, [], PackOptionsStatus.Valid, null);
        }

        var packs = document.GetStringList("resourcePacks");
        var incompatible = document.GetStringList("incompatibleResourcePacks");
        if (!packs.IsSuccess)
        {
            return new KindSelection(
                [],
                [],
                PackOptionsStatus.Unreadable,
                $"{packs.Error!.Message} 为避免覆盖用户配置，未修改该文件。");
        }

        if (!incompatible.IsSuccess)
        {
            return new KindSelection(
                [],
                [],
                PackOptionsStatus.Unreadable,
                $"{incompatible.Error!.Message} 为避免覆盖用户配置，未修改该文件。");
        }

        return new KindSelection(packs.Value!, incompatible.Value!, PackOptionsStatus.Valid, null);
    }

    private static bool IsIncompatible(
        IReadOnlyList<string> incompatibleEntries,
        ContentPackKind kind,
        string fileName) =>
        kind == ContentPackKind.ResourcePack &&
        incompatibleEntries.Any(entry => MatchesFile(kind, entry, fileName));

    private static bool MatchesFile(ContentPackKind kind, string entry, string fileName)
    {
        if (kind == ContentPackKind.ShaderPack)
        {
            return string.Equals(entry, fileName, StringComparison.OrdinalIgnoreCase);
        }

        var name = ResourcePackFileName(entry);
        return name is not null && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 <c>file/&lt;文件名&gt;.zip</c> 还原为文件名；非本模块管理的条目返回 <c>null</c>。</summary>
    private static string? ResourcePackFileName(string entry)
    {
        if (!entry.StartsWith(ResourcePackEntryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = entry[ResourcePackEntryPrefix.Length..];
        if (name.Length == 0 ||
            name.Contains('/') ||
            name.Contains('\\') ||
            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName is not "." and not ".." &&
        fileName.IndexOfAny(['/', '\\']) < 0;

    private static ContentPackEntry ReadEntry(
        string path,
        ContentPackKind kind,
        bool enabled,
        bool incompatible)
    {
        var fileName = Path.GetFileName(path);
        string name = Path.GetFileNameWithoutExtension(fileName);
        string? description = null;
        int? packFormat = null;

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, "pack.mcmeta", StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                using var stream = entry.Open();
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("pack", out var pack))
                {
                    if (pack.TryGetProperty("pack_format", out var format) &&
                        format.ValueKind == JsonValueKind.Number &&
                        format.TryGetInt32(out var parsedFormat))
                    {
                        packFormat = parsedFormat;
                    }

                    description = ReadDescription(pack);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or JsonException or UnauthorizedAccessException)
        {
            // 无效压缩包仍作为文件列出，导入时可以重新覆盖。
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            name = description;
        }

        return new ContentPackEntry(fileName, Path.GetFullPath(path), kind, name, description, packFormat, enabled, incompatible);
    }

    private static string? ReadDescription(JsonElement pack)
    {
        if (!pack.TryGetProperty("description", out var description))
        {
            return null;
        }

        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object when description.TryGetProperty("text", out var text) &&
                                      text.ValueKind == JsonValueKind.String => text.GetString(),
            _ => description.GetRawText(),
        };
    }

    private string GetDirectory(GameInstance instance, ContentPackKind kind)
    {
        var name = kind == ContentPackKind.ShaderPack ? "shaderpacks" : "resourcepacks";
        return Path.Combine(InstancePaths.GetGameDirectory(paths, instance), name);
    }

    private string GetOptionsPath(GameInstance instance) =>
        Path.Combine(InstancePaths.GetGameDirectory(paths, instance), "options.txt");

    private sealed record KindSelection(
        IReadOnlyList<string> Enabled,
        IReadOnlyList<string> Incompatible,
        PackOptionsStatus Status,
        string? Warning);
}
