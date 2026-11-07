// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.World.Nbt;
using Ecl.World.Worlds;

namespace Ecl.Minecraft.World;

/// <summary>
/// 数据包安装目标（`download-resource-template.md` §5.3、`resource-library-template.md` §5.1）：
/// 稳定 InstanceId + 由 <see cref="WorldSaveService"/> 发现的世界存档目录名。
/// <para>
/// 目标身份只能由世界发现结果构造，界面不得用世界显示名称或路径自行拼接；
/// 每次读写前都会重新扫描该实例的世界并重新验证目标仍属于这个 InstanceId，
/// 世界被删除、改名或不可读时拒绝写入。
/// </para>
/// </summary>
public sealed record DatapackTarget(string InstanceId, string FolderName)
{
    public override string ToString() => FolderName;
}

/// <summary>数据包文件的元数据读取状态；页面按资源库模板的颜色语义呈现。</summary>
public enum DatapackReadState
{
    /// <summary><c>pack.mcmeta</c> 可读。</summary>
    Ok,

    /// <summary>文件是有效压缩包，但没有可识别的数据包元数据；不猜测内容。</summary>
    Unrecognized,

    /// <summary>压缩包损坏或元数据无法解析。</summary>
    Corrupt,
}

/// <summary>世界 <c>level.dat</c> 中数据包配置的读取状态。</summary>
public enum DatapackSelectionStatus
{
    /// <summary>世界还没有 <c>DataPacks/Enabled</c>（旧世界）；首次应用时按默认 <c>vanilla</c> 写入。</summary>
    NotInitialized,

    /// <summary>配置可读。</summary>
    Valid,
}

/// <summary>磁盘上的一个数据包文件及其在世界 <c>level.dat</c> 中的启用状态。</summary>
public sealed record DatapackEntry(
    string FileName,
    string FullPath,
    string Name,
    string? Description,
    int? PackFormat,
    bool Enabled)
{
    /// <summary>元数据读取状态；<see cref="DatapackReadState.Corrupt"/> 对应“文件损坏”。</summary>
    public DatapackReadState ReadState { get; init; } = DatapackReadState.Ok;

    /// <summary><c>pack.mcmeta</c> 声明的格式范围；未声明时为 <c>null</c>，不按经验补全。</summary>
    public PackFormatRange? SupportedFormats { get; init; }
}

/// <summary>
/// 一次数据包列表查询的结果。
/// <para>
/// <see cref="MissingEntries"/> 是 level.dat 中已启用、但磁盘上没有对应文件的条目；
/// <see cref="UnmanagedEntries"/> 是非 <c>file/</c> 条目的内置数据包（例如 <c>vanilla</c>、加载器提供的包）；
/// <see cref="DuplicateEntries"/> 是 level.dat Enabled 中重复出现的条目。
/// 三者都只用于提示，不会在未发生显式写入时自动清理。
/// </para>
/// </summary>
public sealed record DatapackList(
    IReadOnlyList<DatapackEntry> Packs,
    IReadOnlyList<string> MissingEntries,
    IReadOnlyList<string> UnmanagedEntries,
    IReadOnlyList<string> DuplicateEntries,
    DatapackSelectionStatus Status,
    string? Warning)
{
    public int EnabledCount => Packs.Count(pack => pack.Enabled);
}

public interface IDatapackService
{
    /// <summary>当前实例可访问的世界清单（`WorldSaveService` 发现结果）。</summary>
    Task<Result<WorldScanResult>> ListWorldsAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>读取指定世界的数据包文件与启用状态；目标在读取前会重新解析验证。</summary>
    Task<Result<DatapackList>> ListAsync(
        string instanceId,
        DatapackTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把给定顺序的启用列表写入该世界 <c>level.dat</c> 的 <c>DataPacks/Enabled</c>。
    /// 非 <c>file/</c> 条目（<c>vanilla</c> 等）保持原位置，不会删除；禁用项从 Enabled/Disabled 中移除，
    /// 不会被补写成“显式禁用”条目。写入经 ADR 0047 的实例事务执行。
    /// </summary>
    Task<Result> ApplyAsync(
        string instanceId,
        DatapackTarget target,
        IReadOnlyList<string> orderedFileNames,
        CancellationToken cancellationToken = default);

    /// <summary>把一个 .zip 数据包复制到世界 <c>datapacks</c>；不修改启用状态。</summary>
    Task<Result<DatapackEntry>> ImportAsync(
        string instanceId,
        DatapackTarget target,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>删除一个数据包文件，同时从该世界 <c>level.dat</c> 清理对应引用。</summary>
    Task<Result> DeleteAsync(
        string instanceId,
        DatapackTarget target,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 数据包领域服务：世界清单、世界级 <c>datapacks</c> 目录读写与 <c>level.dat</c> 的数据包配置。
/// <para>
/// 边界（ADR 0026/0027、ADR 0047、`resource-library-template.md` §5）：
/// - 数据包属于具体世界，不写入实例全局配置；没有可解析的世界目标时拒绝一切写入。
/// - 所有写入都经 <see cref="InstanceTransactionCoordinator"/>，目标限制在该实例游戏目录内。
/// - 启用状态和顺序保存在世界 <c>level.dat</c> 的 <c>DataPacks</c>，与资源包的 <c>options.txt</c> 完全分离。
/// - 只管理 <c>file/&lt;文件名&gt;.zip</c> 条目；<c>vanilla</c> 与加载器条目保留原位置。
/// </para>
/// </summary>
public sealed class DatapackService(
    IInstanceService instances,
    IAppPaths paths,
    InstanceTransactionCoordinator transactions) : IDatapackService
{
    private const string DatapackEntryPrefix = "file/";
    private const string VanillaDataPack = "vanilla";

    /// <summary>加载器或原版内置的数据包标识；保留但不视为用户文件条目。</summary>
    private static readonly HashSet<string> BuiltInDataPacks = new(StringComparer.OrdinalIgnoreCase)
    {
        VanillaDataPack,
        "fabric",
        "forge",
        "neoforge",
        "quilt",
    };

    public async Task<Result<WorldScanResult>> ListWorldsAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result<WorldScanResult>.Fail(resolved.Error!);
        }

        var scan = await new WorldSaveService(resolved.Value!.GameDirectory)
            .ScanAsync(cancellationToken)
            .ConfigureAwait(false);

        // 数据包必须能读取 level.dat 才能确定启用状态；无法读取的世界不出现在目标清单里，
        // 但仍以警告说明它们被排除，避免静默消失。
        var usable = scan.Worlds
            .Where(world => world.Level.IsReadable)
            .ToArray();
        return Result<WorldScanResult>.Ok(new WorldScanResult(usable, scan.Warnings, scan.IsAccessible));
    }

    public async Task<Result<DatapackList>> ListAsync(
        string instanceId,
        DatapackTarget target,
        CancellationToken cancellationToken = default)
    {
        var world = await ResolveWorldAsync(instanceId, target, cancellationToken).ConfigureAwait(false);
        if (!world.IsSuccess)
        {
            return Result<DatapackList>.Fail(world.Error!);
        }

        var resolved = world.Value!;
        if (!resolved.Save.Level.IsReadable)
        {
            return Result<DatapackList>.Fail(
                "datapack.level_unreadable",
                resolved.Save.Level.Error ?? "世界 level.dat 无法读取，无法确定数据包配置。");
        }

        var enabledEntries = resolved.Save.Level.DataPacksEnabled;
        var duplicates = enabledEntries
            .GroupBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var enabledFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmanaged = new List<string>();
        foreach (var entry in enabledEntries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = DatapackFileName(entry);
            if (fileName is null)
            {
                if (!BuiltInDataPacks.Contains(entry))
                {
                    unmanaged.Add(entry);
                }

                continue;
            }

            enabledFileNames.Add(fileName);
        }

        var directory = Path.Combine(resolved.Save.DirectoryPath, "datapacks");
        string[] files;
        try
        {
            files = Directory.Exists(directory)
                ? Directory
                    .EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<DatapackList>.Fail(
                "datapack.scan_failed",
                $"数据包目录无法读取：{exception.Message}");
        }

        var packs = files
            .Select(file => ReadEntry(
                file,
                enabledFileNames.Contains(Path.GetFileName(file))))
            .ToArray();

        var existingNames = new HashSet<string>(
            files.Select(file => Path.GetFileName(file)),
            StringComparer.OrdinalIgnoreCase);
        var missing = enabledFileNames
            .Where(name => !existingNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => DatapackEntryPrefix + name)
            .ToArray();

        var status = resolved.Save.Level.HasDataPacks
            ? DatapackSelectionStatus.Valid
            : DatapackSelectionStatus.NotInitialized;
        return Result<DatapackList>.Ok(new DatapackList(
            packs,
            missing,
            unmanaged,
            duplicates,
            status,
            null));
    }

    public async Task<Result> ApplyAsync(
        string instanceId,
        DatapackTarget target,
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
                return Result.Fail("datapack.invalid_file", "文件名不合法。");
            }
        }

        var world = await ResolveWorldAsync(instanceId, target, cancellationToken).ConfigureAwait(false);
        if (!world.IsSuccess)
        {
            return Result.Fail(world.Error!);
        }

        var resolved = world.Value!;
        var directory = Path.Combine(resolved.Save.DirectoryPath, "datapacks");
        foreach (var fileName in desired)
        {
            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                return Result.Fail("datapack.not_found", $"文件不存在：{fileName}");
            }
        }

        var built = TryBuildLevelDat(resolved.Save.LevelDatPath, desired, out var content);
        if (!built.IsSuccess)
        {
            return Result.Fail(built.Error!);
        }

        if (!built.Value!.Dirty)
        {
            return Result.Ok();
        }

        var relative = RelativeToGameDirectory(resolved.GameDirectory, resolved.Save.LevelDatPath);
        var plan = new InstanceChangePlan(
            [InstanceChangeOperation.WriteBytes(relative, content!)],
            $"更新世界 {resolved.Save.FolderName} 的数据包启用顺序");
        return await ExecuteAsync(instanceId, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DatapackEntry>> ImportAsync(
        string instanceId,
        DatapackTarget target,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return Result<DatapackEntry>.Fail("datapack.not_found", $"文件不存在：{sourcePath}");
        }

        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Result<DatapackEntry>.Fail("datapack.invalid_file", "只支持 .zip 格式的数据包。");
        }

        var fileName = Path.GetFileName(sourcePath);
        if (!IsValidFileName(fileName))
        {
            return Result<DatapackEntry>.Fail("datapack.invalid_file", "文件名不合法。");
        }

        var world = await ResolveWorldAsync(instanceId, target, cancellationToken).ConfigureAwait(false);
        if (!world.IsSuccess)
        {
            return Result<DatapackEntry>.Fail(world.Error!);
        }

        var resolved = world.Value!;
        var destination = Path.Combine(resolved.Save.DirectoryPath, "datapacks", fileName);
        if (File.Exists(destination) && !overwrite)
        {
            return Result<DatapackEntry>.Fail("datapack.target_exists", $"目标已存在：{fileName}");
        }

        var relative = RelativeToGameDirectory(resolved.GameDirectory, destination);
        var plan = new InstanceChangePlan(
            [InstanceChangeOperation.CopyFrom(relative, Path.GetFullPath(sourcePath))],
            $"向世界 {resolved.Save.FolderName} 添加数据包 {fileName}");
        var executed = await ExecuteAsync(instanceId, plan, cancellationToken).ConfigureAwait(false);
        if (!executed.IsSuccess)
        {
            return Result<DatapackEntry>.Fail(executed.Error!);
        }

        var enabled = resolved.Save.Level.DataPacksEnabled
            .Any(entry => string.Equals(
                DatapackFileName(entry),
                fileName,
                StringComparison.OrdinalIgnoreCase));
        return Result<DatapackEntry>.Ok(ReadEntry(destination, enabled));
    }

    public async Task<Result> DeleteAsync(
        string instanceId,
        DatapackTarget target,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidFileName(fileName))
        {
            return Result.Fail("datapack.invalid_file", "文件名不合法。");
        }

        var world = await ResolveWorldAsync(instanceId, target, cancellationToken).ConfigureAwait(false);
        if (!world.IsSuccess)
        {
            return Result.Fail(world.Error!);
        }

        var resolved = world.Value!;
        var destination = Path.Combine(resolved.Save.DirectoryPath, "datapacks", fileName);
        if (!File.Exists(destination))
        {
            return Result.Fail("datapack.not_found", $"文件不存在：{fileName}");
        }

        // 删除文件与清理 level.dat 引用必须在同一次事务里完成；无法解析 level.dat 时拒绝删除，
        // 避免留下一个指向不存在文件的世界配置。
        var pruned = TryPruneLevelDat(resolved.Save.LevelDatPath, fileName, out var content);
        if (!pruned.IsSuccess)
        {
            return Result.Fail(pruned.Error!);
        }

        var operations = new List<InstanceChangeOperation>();
        if (pruned.Value!.Dirty)
        {
            operations.Add(InstanceChangeOperation.WriteBytes(
                RelativeToGameDirectory(resolved.GameDirectory, resolved.Save.LevelDatPath),
                content!));
        }

        operations.Add(InstanceChangeOperation.Delete(
            RelativeToGameDirectory(resolved.GameDirectory, destination)));

        var plan = new InstanceChangePlan(
            operations,
            $"删除世界 {resolved.Save.FolderName} 的数据包 {fileName}");
        return await ExecuteAsync(instanceId, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ResolvedWorld>> ResolveWorldAsync(
        string instanceId,
        DatapackTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!string.Equals(target.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return Result<ResolvedWorld>.Fail(
                "datapack.target_instance_mismatch",
                "数据包目标不属于当前实例。");
        }

        if (string.IsNullOrWhiteSpace(target.FolderName))
        {
            return Result<ResolvedWorld>.Fail("datapack.invalid_target", "没有选择世界目标。");
        }

        var instance = await ResolveInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!instance.IsSuccess)
        {
            return Result<ResolvedWorld>.Fail(instance.Error!);
        }

        var gameDirectory = instance.Value!.GameDirectory;
        var service = new WorldSaveService(gameDirectory);
        var scan = await service.ScanAsync(cancellationToken).ConfigureAwait(false);
        if (!scan.IsAccessible)
        {
            return Result<ResolvedWorld>.Fail(
                "datapack.world_scan_failed",
                scan.Warnings.FirstOrDefault() ?? "世界目录无法读取。");
        }

        var world = scan.Worlds.FirstOrDefault(item =>
            string.Equals(item.FolderName, target.FolderName, StringComparison.OrdinalIgnoreCase));
        if (world is null)
        {
            return Result<ResolvedWorld>.Fail(
                "datapack.world_missing",
                $"世界「{target.FolderName}」已不存在或不在该实例中，请刷新后重新选择。");
        }

        if (!WorldPathSafety.TryNormalizeUnder(service.SavesDirectory, world.DirectoryPath, out _))
        {
            return Result<ResolvedWorld>.Fail(
                "datapack.world_outside_saves",
                "世界目录不在该实例的 saves 范围内，已拒绝写入。");
        }

        if (IsReparsePoint(world.DirectoryPath))
        {
            return Result<ResolvedWorld>.Fail(
                "datapack.world_reparse",
                "世界目录是重解析点，已拒绝写入。");
        }

        return Result<ResolvedWorld>.Ok(new ResolvedWorld(instance.Value!.Instance, world, gameDirectory));
    }

    private async Task<Result<ResolvedInstance>> ResolveInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<ResolvedInstance>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ResolvedInstance>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
        return Result<ResolvedInstance>.Ok(new ResolvedInstance(instance, gameDirectory));
    }

    /// <summary>
    /// 计算新的 level.dat 内容。只重建受管的 <c>file/</c> 条目与清理 Disabled 中的受管条目，
    /// 保留 <c>vanilla</c>/加载器条目及其相对位置；没有任何变化时不写盘。
    /// </summary>
    private static Result<LevelDatUpdate> TryBuildLevelDat(
        string levelDatPath,
        IReadOnlyList<string> desiredFileNames,
        out byte[]? content)
    {
        content = null;
        if (!File.Exists(levelDatPath))
        {
            return Result<LevelDatUpdate>.Fail("datapack.level_missing", "世界缺少 level.dat。");
        }

        NbtTag root;
        try
        {
            root = NbtFile.ReadFile(levelDatPath);
        }
        catch (Exception exception) when (exception is NbtFormatException or IOException
                                              or UnauthorizedAccessException or InvalidDataException)
        {
            return Result<LevelDatUpdate>.Fail(
                "datapack.level_unreadable",
                $"level.dat 无法解析，已保持原文件不变：{exception.Message}");
        }

        var data = root.Child("Data");
        if (data is null)
        {
            return Result<LevelDatUpdate>.Fail(
                "datapack.level_invalid",
                "level.dat 缺少 Data 复合标签，已拒绝写入。");
        }

        var dataPacks = data.Child("DataPacks");
        var enabledTag = dataPacks?.Child("Enabled");
        var currentEnabled = ReadStringList(enabledTag);
        var desiredEntries = desiredFileNames
            .Select(fileName => DatapackEntryPrefix + fileName)
            .ToArray();
        var rebuilt = RebuildEnabled(currentEnabled, enabledTag is not null, desiredEntries);
        var currentDisabled = ReadStringList(dataPacks?.Child("Disabled"));
        var rebuiltDisabled = currentDisabled
            .Where(entry => DatapackFileName(entry) is null)
            .ToArray();

        var enabledChanged = !SequenceEqual(currentEnabled, rebuilt);
        var disabledChanged = !SequenceEqual(currentDisabled, rebuiltDisabled);
        if (!enabledChanged && !disabledChanged)
        {
            return Result<LevelDatUpdate>.Ok(new LevelDatUpdate(false));
        }

        if (dataPacks is null)
        {
            dataPacks = NbtTag.Compound("DataPacks");
            data.Add(dataPacks);
        }

        var writeEnabled = TrySetStringList(dataPacks, "Enabled", rebuilt);
        if (!writeEnabled.IsSuccess)
        {
            return Result<LevelDatUpdate>.Fail(writeEnabled.Error!);
        }

        if (disabledChanged)
        {
            var writeDisabled = TrySetStringList(dataPacks, "Disabled", rebuiltDisabled);
            if (!writeDisabled.IsSuccess)
            {
                return Result<LevelDatUpdate>.Fail(writeDisabled.Error!);
            }
        }

        content = SerializeLevelDat(root);
        return Result<LevelDatUpdate>.Ok(new LevelDatUpdate(true));
    }

    /// <summary>从 level.dat 中移除一个数据包文件的引用；不删除文件本身。</summary>
    private static Result<LevelDatUpdate> TryPruneLevelDat(
        string levelDatPath,
        string fileName,
        out byte[]? content)
    {
        content = null;
        if (!File.Exists(levelDatPath))
        {
            return Result<LevelDatUpdate>.Fail("datapack.level_missing", "世界缺少 level.dat。");
        }

        NbtTag root;
        try
        {
            root = NbtFile.ReadFile(levelDatPath);
        }
        catch (Exception exception) when (exception is NbtFormatException or IOException
                                              or UnauthorizedAccessException or InvalidDataException)
        {
            return Result<LevelDatUpdate>.Fail(
                "datapack.level_unreadable",
                $"level.dat 无法解析，已拒绝删除：{exception.Message}");
        }

        var data = root.Child("Data");
        var dataPacks = data?.Child("DataPacks");
        if (data is null || dataPacks is null)
        {
            return Result<LevelDatUpdate>.Ok(new LevelDatUpdate(false));
        }

        var entry = DatapackEntryPrefix + fileName;
        var enabledTag = dataPacks.Child("Enabled");
        var currentEnabled = ReadStringList(enabledTag);
        var rebuiltEnabled = currentEnabled
            .Where(candidate => !string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var disabledTag = dataPacks.Child("Disabled");
        var currentDisabled = ReadStringList(disabledTag);
        var rebuiltDisabled = currentDisabled
            .Where(candidate => !string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var enabledChanged = !SequenceEqual(currentEnabled, rebuiltEnabled);
        var disabledChanged = !SequenceEqual(currentDisabled, rebuiltDisabled);
        if (!enabledChanged && !disabledChanged)
        {
            return Result<LevelDatUpdate>.Ok(new LevelDatUpdate(false));
        }

        if (enabledChanged)
        {
            var writeEnabled = TrySetStringList(dataPacks, "Enabled", rebuiltEnabled);
            if (!writeEnabled.IsSuccess)
            {
                return Result<LevelDatUpdate>.Fail(writeEnabled.Error!);
            }
        }

        if (disabledChanged)
        {
            var writeDisabled = TrySetStringList(dataPacks, "Disabled", rebuiltDisabled);
            if (!writeDisabled.IsSuccess)
            {
                return Result<LevelDatUpdate>.Fail(writeDisabled.Error!);
            }
        }

        content = SerializeLevelDat(root);
        return Result<LevelDatUpdate>.Ok(new LevelDatUpdate(true));
    }

    /// <summary>
    /// 重建 Enabled：保留非 <c>file/</c> 条目的原位置，把所有受管条目替换为一组目标顺序。
    /// 键不存在时按游戏默认值 <c>vanilla</c> 起头；目标为空时保持原样，不制造空配置。
    /// </summary>
    private static IReadOnlyList<string> RebuildEnabled(
        IReadOnlyList<string> current,
        bool hasEnabledTag,
        IReadOnlyList<string> desiredEntries)
    {
        if (!hasEnabledTag)
        {
            if (desiredEntries.Count == 0)
            {
                return current;
            }

            return desiredEntries
                .Prepend(VanillaDataPack)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var rebuilt = new List<string>(current.Count + desiredEntries.Count);
        var inserted = false;
        foreach (var candidate in current)
        {
            if (DatapackFileName(candidate) is null)
            {
                rebuilt.Add(candidate);
                continue;
            }

            if (!inserted)
            {
                rebuilt.AddRange(desiredEntries);
                inserted = true;
            }
        }

        if (!inserted && desiredEntries.Count > 0)
        {
            rebuilt.AddRange(desiredEntries);
        }

        return rebuilt;
    }

    private static Result TrySetStringList(NbtTag parent, string name, IReadOnlyList<string> values)
    {
        var tag = parent.Child(name);
        if (tag is null)
        {
            var list = NbtTag.List(name, NbtTagType.String);
            foreach (var value in values)
            {
                list.AddTo(new NbtTag(NbtTagType.String, string.Empty, value));
            }

            parent.Add(list);
            return Result.Ok();
        }

        if (tag.Type != NbtTagType.List ||
            (tag.Children.Count > 0 && tag.ListType != NbtTagType.String))
        {
            return Result.Fail(
                "datapack.unsupported_level_format",
                $"level.dat 的 DataPacks.{name} 不是字符串列表，已拒绝写入。");
        }

        tag.ListType = NbtTagType.String;
        tag.ReplaceChildren(values.Select(value => new NbtTag(NbtTagType.String, string.Empty, value)));
        return Result.Ok();
    }

    private static IReadOnlyList<string> ReadStringList(NbtTag? list)
    {
        if (list is not { Type: NbtTagType.List } || list.ListType != NbtTagType.String)
        {
            return [];
        }

        return list.Children
            .Select(child => child.GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static byte[] SerializeLevelDat(NbtTag root)
    {
        using var stream = new MemoryStream();
        NbtFile.Write(stream, root, NbtCompression.Gzip);
        return stream.ToArray();
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    private static DatapackEntry ReadEntry(string path, bool enabled)
    {
        var fileName = Path.GetFileName(path);
        string name = Path.GetFileNameWithoutExtension(fileName);
        string? description = null;
        int? packFormat = null;
        PackFormatRange? supportedFormats = null;
        var readState = DatapackReadState.Unrecognized;

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

                    supportedFormats = ReadSupportedFormats(pack);
                    description = ReadDescription(pack);
                    readState = DatapackReadState.Ok;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or JsonException or UnauthorizedAccessException)
        {
            // 损坏的压缩包仍作为文件列出（状态为“文件损坏”），删除或重新导入由用户决定。
            readState = DatapackReadState.Corrupt;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            name = description;
        }

        return new DatapackEntry(
            fileName,
            Path.GetFullPath(path),
            name,
            description,
            packFormat,
            enabled)
        {
            ReadState = readState,
            SupportedFormats = supportedFormats,
        };
    }

    /// <summary>
    /// 读取 Minecraft 1.20.2+ 的 <c>pack.supported_formats</c>；对象与数组两种写法都支持，
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

    /// <summary>把 <c>file/&lt;文件名&gt;.zip</c> 还原为文件名；非本模块管理的条目返回 <c>null</c>。</summary>
    private static string? DatapackFileName(string entry)
    {
        if (!entry.StartsWith(DatapackEntryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = entry[DatapackEntryPrefix.Length..];
        return IsValidFileName(name) ? name : null;
    }

    private static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName is not "." and not ".." &&
        fileName.IndexOfAny(['/', '\\']) < 0 &&
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string RelativeToGameDirectory(string gameDirectory, string fullPath) =>
        Path.GetRelativePath(gameDirectory, fullPath).Replace('\\', '/');

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

    private async Task<Result> ExecuteAsync(
        string instanceId,
        InstanceChangePlan plan,
        CancellationToken cancellationToken)
    {
        var result = await transactions.ExecuteAsync(instanceId, plan, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Result.Ok() : Result.Fail(result.Error!);
    }

    private sealed record ResolvedInstance(GameInstance Instance, string GameDirectory);

    private sealed record ResolvedWorld(GameInstance Instance, WorldSave Save, string GameDirectory);

    private sealed record LevelDatUpdate(bool Dirty);
}
