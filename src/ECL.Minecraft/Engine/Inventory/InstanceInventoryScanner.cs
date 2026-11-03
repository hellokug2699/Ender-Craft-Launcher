// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Inventory;

/// <summary>
/// 共享 Instance Inventory 扫描器（ADR 0042 §14、ADR 0045）。打开 Explorer 或实例工作台时复用同一份结果，
/// 不各自维护冲突的模组数量与资源状态。
/// </summary>
public sealed class InstanceInventoryScanner : IInstanceInventoryService
{
    private static readonly HashSet<string> SchematicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".schem",
        ".schematic",
        ".litematic",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".json5",
        ".toml",
        ".cfg",
        ".conf",
        ".properties",
        ".yaml",
        ".yml",
        ".txt",
        ".ini",
    };

    private readonly IInstanceService _instances;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceInventoryScanner> _logger;

    public InstanceInventoryScanner(
        IInstanceService instances,
        IAppPaths paths,
        ILogger<InstanceInventoryScanner>? logger = null)
    {
        _instances = instances;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceInventoryScanner>.Instance;
    }

    public string CachePath(string instanceId) =>
        Path.Combine(_paths.CacheDirectory, "instance-engine", "inventory", $"{instanceId}.json");

    public async Task<Result<InstanceInventory>> ScanAsync(
        string instanceId,
        InventoryScanOptions? options = null,
        IProgress<InventoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceInventory>.Fail("inventory.invalid_instance", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceInventory>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        if (!Directory.Exists(gameDirectory))
        {
            return Result<InstanceInventory>.Fail("inventory.root_missing", "实例游戏目录不存在。");
        }

        options ??= new InventoryScanOptions();
        var started = Stopwatch.StartNew();
        var warnings = new List<string>();
        var quick = await QuickScanAsync(gameDirectory, instanceId, options, warnings, progress, cancellationToken)
            .ConfigureAwait(false);

        var resources = new List<InventoryResource>();
        AddMods(instance, gameDirectory, quick, options, resources, warnings, progress, cancellationToken);
        AddPacks(instance, gameDirectory, quick, resources, warnings, cancellationToken);
        AddWorlds(instance, quick, resources);
        AddSchematics(quick, resources);
        AddCrashReports(quick, resources);
        AddLogs(quick, resources);
        AddConfigs(quick, resources, warnings);

        if (resources.Count > options.MaxResources)
        {
            warnings.Add($"资源条目超过 {options.MaxResources}，后续条目未纳入 Inventory。");
            resources = resources.Take(options.MaxResources).ToList();
        }

        var totals = new InventoryTotals(
            quick.Files,
            quick.Directories,
            quick.Bytes,
            quick.Inaccessible,
            resources.Count(item => item.Kind == InventoryResourceKind.Mod),
            resources.Count(item => item.Kind == InventoryResourceKind.Mod &&
                                    item.Enablement == ResourceEnablement.Enabled),
            resources.Count(item => item.Kind == InventoryResourceKind.ResourcePack),
            resources.Count(item => item.Kind == InventoryResourceKind.ShaderPack),
            resources.Count(item => item.Kind == InventoryResourceKind.World),
            resources.Count(item => item.Kind == InventoryResourceKind.Schematic),
            resources.Count(item => item.Kind == InventoryResourceKind.CrashReport),
            resources.Count(item => item.Kind == InventoryResourceKind.Config));

        var evidence = new List<EvidenceItem>
        {
            EvidenceItem.Confirmed(
                "instance",
                "identity",
                $"实例 {instance.Name}（{instance.Id}），Minecraft {instance.GameVersion}，加载器 {instance.Loader}。"),
            EvidenceItem.Confirmed(
                "inventory",
                "files",
                $"快速扫描 {quick.Files} 个文件、{quick.Directories} 个目录、{quick.Bytes} 字节；不可访问 {quick.Inaccessible} 项。"),
        };
        if (quick.Truncated)
        {
            evidence.Add(new EvidenceItem(
                "inventory",
                "limits",
                EvidenceConfidence.Partial,
                $"扫描达到 {options.MaxFiles} 个文件上限，结果不完整。"));
        }

        var inventory = new InstanceInventory(
            InstanceEngineVersions.InventorySchema,
            instance.Id,
            gameDirectory,
            instance.GameVersion,
            instance.Loader,
            instance.LoaderVersion,
            DateTimeOffset.UtcNow,
            InstanceEngineVersions.Scanner,
            InstanceEngineVersions.Rules,
            totals,
            resources,
            evidence,
            warnings,
            !quick.Truncated && quick.Inaccessible == 0);

        await SaveCacheAsync(inventory, cancellationToken).ConfigureAwait(false);
        progress?.Report(new InventoryScanProgress("complete", resources.Count, resources.Count));
        _logger.LogInformation(
            "实例 {InstanceId} Inventory 扫描完成：{Resources} 项资源，{Files} 个文件，耗时 {Elapsed}。",
            instanceId,
            resources.Count,
            quick.Files,
            started.Elapsed);
        return Result<InstanceInventory>.Ok(inventory);
    }

    public async Task<Result<InstanceInventory?>> GetCachedAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var path = CachePath(instanceId);
        if (!File.Exists(path))
        {
            return Result<InstanceInventory?>.Ok(null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var inventory = JsonSerializer.Deserialize<InstanceInventory>(json, EclJson.Default);
            if (inventory is null ||
                inventory.SchemaVersion != InstanceEngineVersions.InventorySchema ||
                inventory.ScannerVersion != InstanceEngineVersions.Scanner ||
                inventory.RulesVersion != InstanceEngineVersions.Rules)
            {
                return Result<InstanceInventory?>.Ok(null);
            }

            var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (instance is null ||
                !string.Equals(inventory.MinecraftVersion, instance.GameVersion, StringComparison.Ordinal) ||
                !string.Equals(inventory.Loader, instance.Loader, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(inventory.LoaderVersion, instance.LoaderVersion, StringComparison.Ordinal))
            {
                return Result<InstanceInventory?>.Ok(null);
            }

            return Result<InstanceInventory?>.Ok(inventory);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取 Inventory 缓存失败：{Path}", path);
            return Result<InstanceInventory?>.Ok(null);
        }
    }

    public Task<Result> InvalidateAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = CachePath(instanceId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Fail("inventory.invalidate_failed", "删除 Inventory 缓存失败。", exception.Message));
        }
    }

    private static async Task<QuickScanResult> QuickScanAsync(
        string gameDirectory,
        string instanceId,
        InventoryScanOptions options,
        List<string> warnings,
        IProgress<InventoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new QuickScanResult();
        var pending = new Stack<string>();
        pending.Push(gameDirectory);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Inaccessible++;
                continue;
            }

            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Inaccessible++;
                    continue;
                }

                var relative = Path.GetRelativePath(gameDirectory, path).Replace('\\', '/');
                if (relative.StartsWith(".ecl/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, ".ecl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"扫描跳过重解析点：{relative}");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    result.Directories++;
                    pending.Push(path);
                    result.AddResourceCandidate(relative, isDirectory: true, size: 0, File.GetLastWriteTimeUtc(path));
                    continue;
                }

                result.Files++;
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Inaccessible++;
                    continue;
                }

                result.Bytes += size;
                result.AddResourceCandidate(relative, isDirectory: false, size, File.GetLastWriteTimeUtc(path));
                if (result.Files % 500 == 0)
                {
                    progress?.Report(new InventoryScanProgress("quick", result.Files, 0, relative));
                }

                if (result.Files >= options.MaxFiles)
                {
                    result.Truncated = true;
                    warnings.Add($"文件数量超过 {options.MaxFiles}，快速扫描提前结束。");
                    pending.Clear();
                    break;
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    private void AddMods(
        GameInstance instance,
        string gameDirectory,
        QuickScanResult quick,
        InventoryScanOptions options,
        List<InventoryResource> resources,
        List<string> warnings,
        IProgress<InventoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var modFiles = quick.Entries
            .Where(entry => !entry.IsDirectory &&
                            entry.RelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                            entry.RelativePath.Count(character => character == '/') == 1 &&
                            (entry.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                             entry.RelativePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["minecraft"] = instance.GameVersion,
        };
        if (!string.IsNullOrWhiteSpace(instance.LoaderVersion))
        {
            available[instance.Loader] = instance.LoaderVersion;
            if (string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase))
            {
                available["fabricloader"] = instance.LoaderVersion;
            }
        }

        var parsed = new List<(QuickEntry Entry, bool Enabled, ModMetadata? Metadata, string? Error)>();
        var processed = 0;
        foreach (var entry in modFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(new InventoryScanProgress("mods", processed, modFiles.Length, entry.RelativePath));
            var fullPath = Path.Combine(gameDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var enabled = !entry.RelativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            ModMetadataReadResult metadata = options.IncludeDeepAnalysis
                ? ModMetadataReader.Read(fullPath)
                : new ModMetadataReadResult(null, "未执行深度分析。");
            parsed.Add((entry, enabled, metadata.Metadata, metadata.Error));
            if (metadata.Metadata is not null)
            {
                available.TryAdd(metadata.Metadata.ModId, metadata.Metadata.Version);
                foreach (var provided in metadata.Metadata.ProvidedIds)
                {
                    available.TryAdd(provided, metadata.Metadata.Version);
                }
            }
        }

        foreach (var (entry, enabled, metadata, error) in parsed)
        {
            var evidence = new List<EvidenceItem>
            {
                new(
                    entry.RelativePath,
                    "mod.file",
                    EvidenceConfidence.Confirmed,
                    enabled ? "文件处于启用状态。" : "文件使用 .disabled 后缀，处于禁用状态。"),
            };
            if (metadata is not null)
            {
                evidence.Add(EvidenceItem.Confirmed(
                    entry.RelativePath,
                    "mod.metadata",
                    $"识别为 {metadata.Platform} Mod，版本 {metadata.Version}。"));
            }
            else if (options.IncludeDeepAnalysis)
            {
                evidence.Add(new EvidenceItem(
                    entry.RelativePath,
                    "mod.metadata",
                    EvidenceConfidence.Failed,
                    error ?? "无法解析模组元数据。"));
            }
            else
            {
                evidence.Add(new EvidenceItem(
                    entry.RelativePath,
                    "mod.metadata",
                    EvidenceConfidence.NotScanned,
                    "未执行深度分析。"));
            }

            var dependencies = metadata is null
                ? []
                : ModCompatibility.EvaluateDependencies(metadata, available);
            if (metadata is not null)
            {
                foreach (var missing in dependencies.Where(item => item.State == DependencyState.Missing))
                {
                    evidence.Add(new EvidenceItem(
                        entry.RelativePath,
                        "mod.dependency",
                        EvidenceConfidence.Confirmed,
                        $"缺少依赖 {missing.ModId}。"));
                }
            }

            string? iconPath = null;
            string? iconSource = null;
            string? iconError = null;
            if (options.IncludeIcons && entry.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(gameDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var icon = ModIconExtractor.Extract(fullPath, _paths.CacheDirectory, cancellationToken);
                iconPath = icon.CachedPath;
                iconSource = icon.Source;
                iconError = icon.Error;
                if (icon.IsSuccess)
                {
                    evidence.Add(EvidenceItem.Confirmed(
                        entry.RelativePath,
                        "mod.icon",
                        $"从 {icon.EntryPath} 提取 {icon.Width}×{icon.Height} 图标。"));
                }
            }

            resources.Add(new InventoryResource(
                $"mod:{entry.RelativePath}",
                InventoryResourceKind.Mod,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                FileTypeRegistry.Detect(entry.RelativePath, ReadOnlySpan<byte>.Empty).Id,
                enabled ? ResourceEnablement.Enabled : ResourceEnablement.Disabled,
                metadata?.Name ?? Path.GetFileName(entry.RelativePath),
                metadata?.Version,
                dependencies,
                ModCompatibility.EvaluateMinecraft(instance, metadata),
                ModCompatibility.EvaluateLoader(instance.Loader, metadata),
                evidence,
                iconPath,
                iconSource,
                iconError));
        }

        if (modFiles.Length == 0)
        {
            warnings.Add("mods 目录中没有 .jar 模组。");
        }
    }

    private static void AddPacks(
        GameInstance instance,
        string gameDirectory,
        QuickScanResult quick,
        List<InventoryResource> resources,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var enabledResourcePacks = ReadEnabledPacks(gameDirectory, "resourcePacks", warnings);
        var enabledShaderPacks = ReadEnabledPacks(gameDirectory, "shaderPacks", warnings, warnWhenMissing: false);

        foreach (var entry in quick.Entries.Where(entry =>
                     entry.RelativePath.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase) &&
                     entry.RelativePath.Count(character => character == '/') == 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry.RelativePath);
            var enabled = enabledResourcePacks is not null &&
                          enabledResourcePacks.Contains(name) ||
                          enabledResourcePacks?.Contains(Path.GetFileNameWithoutExtension(name)) == true;
            resources.Add(new InventoryResource(
                $"resourcepack:{entry.RelativePath}",
                InventoryResourceKind.ResourcePack,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                entry.IsDirectory ? "inode/directory" : FileTypeRegistry.Detect(name, ReadOnlySpan<byte>.Empty).Id,
                enabledResourcePacks is null
                    ? ResourceEnablement.Unknown
                    : enabled ? ResourceEnablement.Enabled : ResourceEnablement.Disabled,
                name,
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [
                    new EvidenceItem(
                        "resourcepacks",
                        "pack.enablement",
                        enabledResourcePacks is null ? EvidenceConfidence.Unknown : EvidenceConfidence.Confirmed,
                        enabledResourcePacks is null
                            ? "无法读取 options.txt，启用状态未知。"
                            : enabled ? "options.txt 的资源包列表包含该项。" : "options.txt 的资源包列表未包含该项。"),
                ]));
        }

        foreach (var entry in quick.Entries.Where(entry =>
                     entry.RelativePath.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase) &&
                     entry.RelativePath.Count(character => character == '/') == 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry.RelativePath);
            var enabled = enabledShaderPacks is not null &&
                          (enabledShaderPacks.Contains(name) ||
                           enabledShaderPacks.Contains(Path.GetFileNameWithoutExtension(name)));
            resources.Add(new InventoryResource(
                $"shaderpack:{entry.RelativePath}",
                InventoryResourceKind.ShaderPack,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                entry.IsDirectory ? "inode/directory" : FileTypeRegistry.Detect(name, ReadOnlySpan<byte>.Empty).Id,
                enabledShaderPacks is null
                    ? ResourceEnablement.Unknown
                    : enabled ? ResourceEnablement.Enabled : ResourceEnablement.Disabled,
                name,
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [
                    new EvidenceItem(
                        "shaderpacks",
                        "pack.enablement",
                        enabledShaderPacks is null ? EvidenceConfidence.Unknown : EvidenceConfidence.Confirmed,
                        enabledShaderPacks is null
                            ? "无法读取 options.txt，光影启用状态未知。"
                            : enabled ? "options.txt 的 shaderPacks 列表包含该项。" : "options.txt 的 shaderPacks 列表未包含该项。"),
                ]));
        }
    }

    private static void AddWorlds(GameInstance instance, QuickScanResult quick, List<InventoryResource> resources)
    {
        foreach (var entry in quick.Entries.Where(entry =>
                     !entry.IsDirectory &&
                     entry.RelativePath.StartsWith("saves/", StringComparison.OrdinalIgnoreCase) &&
                     entry.RelativePath.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = entry.RelativePath.Split('/');
            if (parts.Length != 3)
            {
                continue;
            }

            resources.Add(new InventoryResource(
                $"world:{parts[1]}",
                InventoryResourceKind.World,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                "minecraft/level",
                ResourceEnablement.Enabled,
                parts[1],
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Compatible,
                [EvidenceItem.Confirmed(entry.RelativePath, "world.level_dat", "saves 目录中存在 level.dat。")],
                WorldName: parts[1]));
        }
    }

    private static void AddSchematics(QuickScanResult quick, List<InventoryResource> resources)
    {
        foreach (var entry in quick.Entries.Where(entry =>
                     !entry.IsDirectory && SchematicExtensions.Contains(Path.GetExtension(entry.RelativePath))))
        {
            resources.Add(new InventoryResource(
                $"schematic:{entry.RelativePath}",
                InventoryResourceKind.Schematic,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                FileTypeRegistry.Detect(entry.RelativePath, ReadOnlySpan<byte>.Empty).Id,
                ResourceEnablement.Unknown,
                Path.GetFileName(entry.RelativePath),
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [EvidenceItem.Confirmed(entry.RelativePath, "schematic.extension", "已知投影/蓝图扩展名。")]));
        }
    }

    private static void AddCrashReports(QuickScanResult quick, List<InventoryResource> resources)
    {
        foreach (var entry in quick.Entries.Where(entry =>
                     !entry.IsDirectory &&
                     entry.RelativePath.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase) &&
                     entry.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Take(50))
        {
            resources.Add(new InventoryResource(
                $"crash:{entry.RelativePath}",
                InventoryResourceKind.CrashReport,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                "text/plain",
                ResourceEnablement.Enabled,
                Path.GetFileName(entry.RelativePath),
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [EvidenceItem.Confirmed(entry.RelativePath, "crash.file", "崩溃报告文件；需由诊断引擎只读解析。")]));
        }
    }

    private static void AddLogs(QuickScanResult quick, List<InventoryResource> resources)
    {
        foreach (var entry in quick.Entries.Where(entry =>
                     !entry.IsDirectory &&
                     entry.RelativePath.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) &&
                     (entry.RelativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                      entry.RelativePath.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase))).Take(50))
        {
            resources.Add(new InventoryResource(
                $"log:{entry.RelativePath}",
                InventoryResourceKind.Log,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                "text/plain",
                ResourceEnablement.Enabled,
                Path.GetFileName(entry.RelativePath),
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [EvidenceItem.Confirmed(entry.RelativePath, "log.file", "游戏日志文件。")]));
        }
    }

    private static void AddConfigs(QuickScanResult quick, List<InventoryResource> resources, List<string> warnings)
    {
        var configs = quick.Entries
            .Where(entry => !entry.IsDirectory &&
                            entry.RelativePath.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
                            ConfigExtensions.Contains(Path.GetExtension(entry.RelativePath)))
            .Take(1000)
            .ToArray();
        if (configs.Length == 1000)
        {
            warnings.Add("配置文件超过 1000 个，仅列出前 1000 个。");
        }

        foreach (var entry in configs)
        {
            resources.Add(new InventoryResource(
                $"config:{entry.RelativePath}",
                InventoryResourceKind.Config,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedAt,
                FileTypeRegistry.Detect(entry.RelativePath, ReadOnlySpan<byte>.Empty).Id,
                ResourceEnablement.Enabled,
                Path.GetFileName(entry.RelativePath),
                null,
                [],
                CompatibilityState.Unknown,
                CompatibilityState.Unknown,
                [EvidenceItem.Confirmed(entry.RelativePath, "config.file", "config 目录中的文本配置。")]));
        }
    }

    private static HashSet<string>? ReadEnabledPacks(
        string gameDirectory,
        string key,
        List<string> warnings,
        bool warnWhenMissing = true)
    {
        var path = Path.Combine(gameDirectory, "options.txt");
        if (!File.Exists(path))
        {
            if (warnWhenMissing)
            {
                warnings.Add("options.txt 不存在，资源包/光影启用状态未知。");
            }

            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(key.Length + 1)..].Trim();
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                return document.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Select(item => item.StartsWith("file/", StringComparison.OrdinalIgnoreCase) ? item[5..] : item)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"读取 options.txt 失败：{exception.Message}");
        }

        return null;
    }

    private async Task SaveCacheAsync(InstanceInventory inventory, CancellationToken cancellationToken)
    {
        try
        {
            var path = CachePath(inventory.InstanceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(inventory, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "写入实例 {InstanceId} 的 Inventory 缓存失败。", inventory.InstanceId);
        }
    }

    private sealed class QuickScanResult
    {
        public int Files { get; set; }

        public int Directories { get; set; }

        public long Bytes { get; set; }

        public int Inaccessible { get; set; }

        public bool Truncated { get; set; }

        public List<QuickEntry> Entries { get; } = [];

        public void AddResourceCandidate(string relative, bool isDirectory, long size, DateTime modifiedAt)
        {
            var firstSlash = relative.IndexOf('/');
            var firstSegment = firstSlash >= 0 ? relative[..firstSlash] : relative;
            if (!IsRelevantDirectory(firstSegment))
            {
                return;
            }

            Entries.Add(new QuickEntry(relative, isDirectory, size, modifiedAt));
        }

        private static bool IsRelevantDirectory(string name) =>
            name.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("crash-reports", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("schematics", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record QuickEntry(string RelativePath, bool IsDirectory, long Size, DateTimeOffset ModifiedAt);
}
