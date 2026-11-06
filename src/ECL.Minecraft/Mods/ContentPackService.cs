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

/// <summary>资源包/光影包文件的元数据读取状态；页面按页面规格的颜色语义呈现。</summary>
public enum ContentPackReadState
{
    /// <summary>元数据可读（资源包有 <c>pack.mcmeta</c>，光影包含 <c>shaders/</c>）。</summary>
    Ok,

    /// <summary>文件是有效压缩包，但没有可识别的元数据；不猜测内容。</summary>
    Unrecognized,

    /// <summary>压缩包损坏或元数据无法解析。</summary>
    Corrupt,
}

/// <summary>存档在 <c>pack.mcmeta</c> 中的声明式格式范围（Minecraft 1.20.2+ 的 <c>supported_formats</c>）。</summary>
public readonly record struct PackFormatRange(int MinInclusive, int MaxInclusive);

/// <summary>磁盘上的一个资源包/光影包文件，以及它在 options.txt 中的启用状态。</summary>
public sealed record ContentPackEntry(
    string FileName,
    string FullPath,
    ContentPackKind Kind,
    string Name,
    string? Description,
    int? PackFormat,
    bool Enabled,
    bool Incompatible)
{
    /// <summary>元数据读取状态；<see cref="ContentPackReadState.Corrupt"/> 对应“文件损坏”。</summary>
    public ContentPackReadState ReadState { get; init; } = ContentPackReadState.Ok;

    /// <summary><c>pack.mcmeta</c> 声明的格式范围；未声明时为 <c>null</c>，不按经验补全。</summary>
    public PackFormatRange? SupportedFormats { get; init; }
}

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

    /// <summary>
    /// 一次写入提交页面的待应用更改（resource-library-template.md §5）：资源包以
    /// <paramref name="orderedFileNames"/> 的顺序重写 <c>resourcePacks</c> 的受管条目
    /// （顺序即页面显示的优先级顺序），光影包只保留最后一个为当前选择。
    /// <para>
    /// 未受管条目（<c>vanilla</c>、其它启动器写入的裸值等）保持原位置，不会被删除；
    /// options.txt 无法解析时拒绝写入，不覆盖用户配置。
    /// </para>
    /// </summary>
    Task<Result> ApplyAsync(
        string instanceId,
        ContentPackKind kind,
        IReadOnlyList<string> orderedFileNames,
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

    public async Task<Result> ApplyAsync(
        string instanceId,
        ContentPackKind kind,
        IReadOnlyList<string> orderedFileNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedFileNames);

        var desired = orderedFileNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var fileName in desired)
        {
            if (!IsValidFileName(fileName))
            {
                return Result.Fail("pack.invalid_file", "文件名不合法。");
            }
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        var directory = GetDirectory(instance, kind);
        foreach (var fileName in desired)
        {
            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                return Result.Fail("pack.not_found", $"文件不存在：{fileName}");
            }
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

        if (kind == ContentPackKind.ShaderPack)
        {
            // 游戏一次只支持一个光影包；页面只允许一行处于“已启用”，这里取最后一个作为当前选择。
            var active = desired.LastOrDefault();
            if (active is null)
            {
                document.Remove("shaderPack");
            }
            else
            {
                document.SetString("shaderPack", active);
            }
        }
        else
        {
            ApplyResourcePackOrder(document, selection, desired);
        }

        return document.IsDirty ? document.Save() : Result.Ok();
    }

    /// <summary>
    /// 按给定顺序重写受管资源包条目，保留 <c>vanilla</c> 与其它未受管条目的原始位置；
    /// 被禁用（不再出现在目标列表）的条目同时清理“已确认不兼容”记录。
    /// </summary>
    private static void ApplyResourcePackOrder(
        GameOptionsFile document,
        KindSelection selection,
        IReadOnlyList<string> desiredFileNames)
    {
        var desiredEntries = desiredFileNames
            .Select(fileName => ResourcePackEntryPrefix + fileName)
            .ToArray();
        var desiredEntrySet = new HashSet<string>(desiredEntries, StringComparer.OrdinalIgnoreCase);

        var rebuilt = new List<string>();
        var inserted = false;
        foreach (var entry in selection.Enabled)
        {
            if (ResourcePackFileName(entry) is null)
            {
                rebuilt.Add(entry);
                continue;
            }

            // 全部受管条目被替换为一次按目标顺序插入的列表，避免逐个追加导致顺序错乱。
            if (!inserted)
            {
                rebuilt.AddRange(desiredEntries);
                inserted = true;
            }
        }

        if (!inserted)
        {
            rebuilt.AddRange(desiredEntries);
        }

        // 键缺失时游戏按默认值 ["vanilla"] 处理；目标列表非空时补上默认项。
        if (document.GetValue("resourcePacks") is null &&
            rebuilt.Count > 0 &&
            !rebuilt.Any(entry => string.Equals(entry, VanillaResourcePack, StringComparison.OrdinalIgnoreCase)))
        {
            rebuilt.Insert(0, VanillaResourcePack);
        }

        document.SetStringList("resourcePacks", rebuilt);

        var disabled = selection.Enabled
            .Select(ResourcePackFileName)
            .Where(name => name is not null && !desiredEntrySet.Contains(ResourcePackEntryPrefix + name))
            .Select(name => ResourcePackEntryPrefix + name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (disabled.Count == 0)
        {
            return;
        }

        var incompatible = selection.Incompatible
            .Where(candidate => !disabled.Contains(candidate))
            .ToArray();
        if (incompatible.Length != selection.Incompatible.Count)
        {
            document.SetStringList("incompatibleResourcePacks", incompatible);
        }
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
        PackFormatRange? supportedFormats = null;
        var readState = ContentPackReadState.Unrecognized;

        try
        {
            using var archive = ZipFile.OpenRead(path);

            if (kind == ContentPackKind.ShaderPack)
            {
                // 光影包没有 pack.mcmeta；有效压缩包至少包含 shaders/ 目录。
                readState = archive.Entries.Any(candidate =>
                        candidate.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
                    ? ContentPackReadState.Ok
                    : ContentPackReadState.Unrecognized;
            }
            else
            {
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

                        supportedFormats = ReadSupportedFormats(pack);
                        description = ReadDescription(pack);
                        readState = ContentPackReadState.Ok;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or JsonException or UnauthorizedAccessException)
        {
            // 损坏的压缩包仍作为文件列出（状态为“文件损坏”），删除或重新导入由用户决定。
            readState = ContentPackReadState.Corrupt;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            name = description;
        }

        return new ContentPackEntry(
            fileName,
            Path.GetFullPath(path),
            kind,
            name,
            description,
            packFormat,
            enabled,
            incompatible)
        {
            ReadState = readState,
            SupportedFormats = supportedFormats,
        };
    }

    /// <summary>
    /// 读取 Minecraft 1.20.2+ 的 <c>pack.supported_formats</c>：可以是
    /// <c>{"min_inclusive":…, "max_inclusive":…}</c>，也可以是 <c>[min, max]</c>。
    /// 无法无歧义解析时返回 <c>null</c>，不猜测范围。
    /// </summary>
    private static PackFormatRange? ReadSupportedFormats(JsonElement pack)
    {
        if (!pack.TryGetProperty("supported_formats", out var formats))
        {
            return null;
        }

        if (formats.ValueKind == JsonValueKind.Object)
        {
            var min = formats.TryGetProperty("min_inclusive", out var minElement) &&
                      minElement.ValueKind == JsonValueKind.Number &&
                      minElement.TryGetInt32(out var parsedMin)
                ? parsedMin
                : (int?)null;
            var max = formats.TryGetProperty("max_inclusive", out var maxElement) &&
                      maxElement.ValueKind == JsonValueKind.Number &&
                      maxElement.TryGetInt32(out var parsedMax)
                ? parsedMax
                : (int?)null;
            return min is null || max is null || max < min ? null : new PackFormatRange(min.Value, max.Value);
        }

        if (formats.ValueKind == JsonValueKind.Array &&
            formats.GetArrayLength() == 2 &&
            formats[0].ValueKind == JsonValueKind.Number &&
            formats[1].ValueKind == JsonValueKind.Number &&
            formats[0].TryGetInt32(out var rangeMin) &&
            formats[1].TryGetInt32(out var rangeMax) &&
            rangeMax >= rangeMin)
        {
            return new PackFormatRange(rangeMin, rangeMax);
        }

        return null;
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
