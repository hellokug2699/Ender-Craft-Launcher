// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Instances;

public sealed partial class InstanceService : IInstanceService
{
    private static readonly HashSet<string> SupportedLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "vanilla",
        "fabric",
        "forge",
        "neoforge",
        "cleanroom",
    };

    /// <summary>版本目录里出现这些条目，说明该版本按“版本隔离”保存着自己的游戏数据。</summary>
    private static readonly string[] IsolationMarkers =
    [
        "mods",
        "config",
        "saves",
        "options.txt",
        "servers.dat",
        "resourcepacks",
        "shaderpacks",
    ];

    private readonly IInstanceStore _store;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceService> _logger;

    public InstanceService(IInstanceStore store, IAppPaths paths, ILogger<InstanceService> logger)
    {
        _store = store;
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        var instances = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var repaired = new List<GameInstance>(instances.Count);

        foreach (var instance in instances)
        {
            var gameVersion = DetectGameVersionForImportedInstance(instance);
            if (gameVersion is not null &&
                !string.Equals(gameVersion, instance.GameVersion, StringComparison.Ordinal))
            {
                // 旧版本导入时会把整合包名写进 GameVersion；这里用版本 JSON 中的
                // clientVersion / 加载器信息修正一次，之后不再重复写入。
                var updated = instance with { GameVersion = gameVersion };
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                repaired.Add(updated);
            }
            else
            {
                repaired.Add(instance);
            }
        }

        return repaired;
    }

    public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _store.GetAsync(instanceId, cancellationToken);

    public async Task<Result<GameInstance>> CreateAsync(
        string name,
        string gameVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        gameVersion = gameVersion.Trim();
        loader = loader.Trim();
        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
        fabricApiVersion = string.IsNullOrWhiteSpace(fabricApiVersion) ? null : fabricApiVersion.Trim();
        optiFineVersion = string.IsNullOrWhiteSpace(optiFineVersion) ? null : optiFineVersion.Trim();

        if (name.Length is < 1 or > 48)
        {
            return Result<GameInstance>.Fail("instance.invalid_name", "实例名称需为 1-48 个字符。");
        }

        if (!VersionRegex().IsMatch(gameVersion))
        {
            return Result<GameInstance>.Fail("instance.invalid_version", "游戏版本格式不正确。");
        }

        if (!SupportedLoaders.Contains(loader))
        {
            return Result<GameInstance>.Fail("instance.invalid_loader", $"不支持的加载器：{loader}");
        }

        if (!string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) && loaderVersion is null)
        {
            return Result<GameInstance>.Fail("instance.loader_version_required", "请选择加载器版本。");
        }

        if (string.Equals(loader, "cleanroom", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(gameVersion, "1.12.2", StringComparison.Ordinal))
        {
            return Result<GameInstance>.Fail("instance.cleanroom_version", "Cleanroom 仅支持 Minecraft 1.12.2。");
        }

        if (loaderVersion is not null && !LoaderVersionRegex().IsMatch(loaderVersion))
        {
            return Result<GameInstance>.Fail("instance.invalid_loader_version", "加载器版本格式不正确。");
        }

        if (fabricApiVersion is not null &&
            (!string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase) ||
             !LoaderVersionRegex().IsMatch(fabricApiVersion)))
        {
            return Result<GameInstance>.Fail("instance.invalid_fabric_api", "Fabric API 版本无效。");
        }

        if (optiFineVersion is not null)
        {
            // OptiFine 以 mod 形式安装：Forge 与 Cleanroom 可用，Fabric/NeoForge/原版不可用。
            if (!string.Equals(loader, "cleanroom", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase))
            {
                return Result<GameInstance>.Fail(
                    "instance.optifine_requires_forge",
                    "OptiFine 仅在 Forge 或 Cleanroom 加载器下可选。");
            }

            if (!OptiFineKeyRegex().IsMatch(optiFineVersion))
            {
                return Result<GameInstance>.Fail("instance.invalid_optifine", "OptiFine 版本格式不正确。");
            }
        }

        var existing = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(instance => string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Result<GameInstance>.Fail("instance.duplicate_name", $"已存在同名实例：{name}");
        }

        var instanceId = await CreateUniqueIdAsync(name, cancellationToken).ConfigureAwait(false);
        var versionId = BuildVersionId(gameVersion, loader, loaderVersion);

        var instance = new GameInstance
        {
            Id = instanceId,
            Name = name,
            GameVersion = gameVersion,
            VersionId = versionId,
            Loader = loader.ToLowerInvariant(),
            LoaderVersion = loaderVersion,
            FabricApiVersion = string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase)
                ? fabricApiVersion
                : null,
            OptiFineVersion = optiFineVersion,
            VersionIsolation = InstanceIsolation.Enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已创建实例 {Name}（{Id}，版本 {VersionId}）。", instance.Name, instance.Id, instance.VersionId);
        return Result<GameInstance>.Ok(instance);
    }

    public async Task<Result<GameInstance>> UpdateConfigurationAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<GameInstance>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var current = await _store.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Result<GameInstance>.Fail("instance.not_found", "实例不存在。");
        }

        gameVersion = gameVersion.Trim();
        loader = loader.Trim().ToLowerInvariant();
        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
        fabricApiVersion = string.IsNullOrWhiteSpace(fabricApiVersion) ? null : fabricApiVersion.Trim();
        optiFineVersion = string.IsNullOrWhiteSpace(optiFineVersion) ? null : optiFineVersion.Trim();

        if (!VersionRegex().IsMatch(gameVersion))
        {
            return Result<GameInstance>.Fail("instance.invalid_version", "游戏版本格式不正确。");
        }

        if (!SupportedLoaders.Contains(loader))
        {
            return Result<GameInstance>.Fail("instance.invalid_loader", $"不支持的加载器：{loader}");
        }

        if (loader == "vanilla" && loaderVersion is not null ||
            loader != "vanilla" && loaderVersion is null)
        {
            return Result<GameInstance>.Fail("instance.loader_version_required", "所选加载器版本无效。");
        }

        if (loader == "cleanroom" && gameVersion != "1.12.2")
        {
            return Result<GameInstance>.Fail("instance.cleanroom_version", "Cleanroom 仅支持 Minecraft 1.12.2。");
        }

        if (loaderVersion is not null && !LoaderVersionRegex().IsMatch(loaderVersion))
        {
            return Result<GameInstance>.Fail("instance.invalid_loader_version", "加载器版本格式不正确。");
        }

        if (fabricApiVersion is not null &&
            (loader != "fabric" || !LoaderVersionRegex().IsMatch(fabricApiVersion)))
        {
            return Result<GameInstance>.Fail("instance.invalid_fabric_api", "Fabric API 版本无效。");
        }

        if (optiFineVersion is not null &&
            ((loader != "forge" && loader != "cleanroom") || !OptiFineKeyRegex().IsMatch(optiFineVersion)))
        {
            return Result<GameInstance>.Fail("instance.invalid_optifine", "OptiFine 仅支持 Forge / Cleanroom，且版本键格式必须有效。");
        }

        var updated = current with
        {
            GameVersion = gameVersion,
            VersionId = BuildVersionId(gameVersion, loader, loaderVersion),
            Loader = loader,
            LoaderVersion = loaderVersion,
            FabricApiVersion = loader == "fabric" ? fabricApiVersion : null,
            OptiFineVersion = optiFineVersion,
        };

        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "已更新实例 {InstanceId} 配置：Minecraft {GameVersion}，加载器 {Loader} {LoaderVersion}。",
            instanceId, gameVersion, loader, loaderVersion);
        return Result<GameInstance>.Ok(updated);
    }

    public async Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default)
    {
        var imported = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(instance => instance.VersionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var versionsDirectory = _paths.VersionsDirectory;
        if (!Directory.Exists(versionsDirectory))
        {
            return [];
        }

        var detected = new List<DetectedVersion>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versionId = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, $"{versionId}.json");
            if (!File.Exists(jsonPath))
            {
                // 只有带同名版本 JSON 的目录才是可启动的版本（assets/、libraries/ 等会被跳过）。
                continue;
            }

            var profile = InspectVersion(versionId);
            var isolated = IsIsolated(directory);
            detected.Add(new DetectedVersion(
                versionId,
                profile.GameVersion ?? versionId,
                profile.Loader,
                isolated,
                isolated ? directory : _paths.GameRoot,
                imported.Contains(versionId),
                profile.Problem,
                profile.ProblemDetail));
        }

        return detected.OrderBy(version => version.VersionId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<Result<GameInstance>> ImportAsync(
        string versionId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Result<GameInstance>.Fail("instance.invalid_version", "版本 ID 不能为空。");
        }

        var candidates = await ScanVersionsAsync(cancellationToken).ConfigureAwait(false);
        var candidate = candidates.FirstOrDefault(item =>
            string.Equals(item.VersionId, versionId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return Result<GameInstance>.Fail(
                "instance.version_not_found",
                $"未在 versions 目录找到带有同名 JSON 的版本：{versionId}");
        }

        if (candidate.AlreadyImported)
        {
            return Result<GameInstance>.Fail("instance.already_imported", $"该版本已经导入过了：{candidate.VersionId}");
        }

        switch (candidate.Problem)
        {
            case DetectedVersionProblem.IncompleteInheritance:
                return Result<GameInstance>.Fail(
                    "instance.version_incomplete",
                    $"版本 {candidate.VersionId} 的安装不完整，无法导入：{candidate.ProblemDetail}");
            case DetectedVersionProblem.InvalidManifest:
                return Result<GameInstance>.Fail(
                    "instance.version_invalid",
                    $"版本 {candidate.VersionId} 无法导入：{candidate.ProblemDetail}");
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? candidate.VersionId : name.Trim();
        if (displayName.Length is < 1 or > 48)
        {
            return Result<GameInstance>.Fail("instance.invalid_name", "实例名称需为 1-48 个字符。");
        }

        var existing = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(instance => string.Equals(instance.Name, displayName, StringComparison.OrdinalIgnoreCase)))
        {
            return Result<GameInstance>.Fail("instance.duplicate_name", $"已存在同名实例：{displayName}");
        }

        var instance = new GameInstance
        {
            Id = await CreateUniqueIdAsync(displayName, cancellationToken).ConfigureAwait(false),
            Name = displayName,
            GameVersion = candidate.GameVersion,
            VersionId = candidate.VersionId,
            Loader = candidate.Loader,
            VersionIsolation = candidate.Isolated,
            GameDirectory = candidate.GameDirectory,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "已导入既有版本 {VersionId} 为实例 {Name}（{Id}），游戏目录：{Directory}。",
            candidate.VersionId,
            instance.Name,
            instance.Id,
            instance.GameDirectory);
        return Result<GameInstance>.Ok(instance);
    }

    private static bool IsIsolated(string versionDirectory) =>
        IsolationMarkers.Any(marker =>
            File.Exists(Path.Combine(versionDirectory, marker)) ||
            Directory.Exists(Path.Combine(versionDirectory, marker)));

    /// <summary>
    /// 导入实例的 GameVersion 可能在旧版本中被写成整合包/版本目录名。
    /// 这里从原安装的版本 JSON 中重新读取实际 Minecraft 版本；仅对导入实例生效。
    /// </summary>
    private string? DetectGameVersionForImportedInstance(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.GameDirectory))
        {
            return null;
        }

        // 正常导入的版本位于 ECL 的 versions 目录：用完整继承链解析，
        // 避免把加载器/整合包配置的名字当成 Minecraft 版本。
        if (File.Exists(Path.Combine(
                _paths.VersionsDirectory,
                instance.VersionId,
                $"{instance.VersionId}.json")))
        {
            var profile = InspectVersion(instance.VersionId);
            return profile.Problem == DetectedVersionProblem.None ? profile.GameVersion : null;
        }

        // 兼容可能引用外部 versions 目录的旧实例：只能读取单层 JSON 的显式字段。
        var candidates = new[]
        {
            Path.Combine(instance.GameDirectory, $"{instance.VersionId}.json"),
            Path.Combine(instance.GameDirectory, "versions", instance.VersionId, $"{instance.VersionId}.json"),
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || !TryReadManifest(path, out var manifest, out _))
            {
                continue;
            }

            // 整合包/加载器版本 JSON 的 id 往往就是整合包名，不能直接当成 Minecraft 版本。
            // 依次尝试启动器写入的 clientVersion、Forge/NeoForge 的 --fml.mcVersion。
            var gameVersion = FirstValidVersion(
                manifest.ClientVersion,
                manifest.ForgeMinecraftVersion);
            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                return gameVersion;
            }
        }

        return null;
    }

    /// <summary>
    /// 从版本目录检查该版本能否作为实例导入：版本 JSON 可解析、继承链完整、能找到 mainClass。
    /// 损坏或不完整的安装（其它启动器下载中断、版本 JSON 为空等）在这里就会被标记，
    /// 不会等到导入之后才在启动阶段失败。
    /// </summary>
    private VersionProfile InspectVersion(string versionId)
    {
        string? gameVersion = null;
        string? explicitGameVersion = null;
        string? rootVersionId = null;
        var loader = "vanilla";
        var hasMainClass = false;
        var first = true;
        var currentId = versionId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (!visited.Add(currentId))
            {
                return new VersionProfile(
                    gameVersion,
                    loader,
                    DetectedVersionProblem.IncompleteInheritance,
                    $"版本继承链存在循环：{currentId}");
            }

            if (!VersionRegex().IsMatch(currentId))
            {
                return new VersionProfile(
                    gameVersion,
                    loader,
                    DetectedVersionProblem.InvalidManifest,
                    $"版本 ID 不合法：{currentId}");
            }

            var jsonPath = Path.Combine(_paths.VersionsDirectory, currentId, $"{currentId}.json");
            if (!File.Exists(jsonPath))
            {
                return first
                    ? new VersionProfile(
                        null,
                        loader,
                        DetectedVersionProblem.InvalidManifest,
                        $"未找到版本文件：{jsonPath}")
                    : new VersionProfile(
                        gameVersion,
                        loader,
                        DetectedVersionProblem.IncompleteInheritance,
                        $"缺少父版本 {currentId} 的版本文件。");
            }

            if (!TryReadManifest(jsonPath, out var manifest, out var error))
            {
                return new VersionProfile(gameVersion, loader, DetectedVersionProblem.InvalidManifest, error);
            }

            if (first)
            {
                loader = DetectLoader(manifest.MainClass ?? string.Empty, manifest.Libraries);
                explicitGameVersion = FirstValidVersion(
                    manifest.ClientVersion,
                    manifest.ForgeMinecraftVersion);
                gameVersion = FirstValidVersion(
                    manifest.ClientVersion,
                    manifest.InheritsFrom,
                    manifest.ForgeMinecraftVersion);
                first = false;
            }

            hasMainClass |= !string.IsNullOrWhiteSpace(manifest.MainClass);
            rootVersionId = currentId;

            if (string.IsNullOrWhiteSpace(manifest.InheritsFrom))
            {
                break;
            }

            currentId = manifest.InheritsFrom.Trim();
        }

        // 直接父版本可能本身也是加载器/整合包配置（多层 inheritsFrom）。
        // 缺少 clientVersion / --fml.mcVersion 时，继承链最底层的版本 ID 才是 Minecraft 版本。
        // Cleanroom 只支持 1.12.2，缺少元数据时按该版本回退。
        gameVersion = explicitGameVersion
                      ?? (string.Equals(loader, "cleanroom", StringComparison.OrdinalIgnoreCase) ? "1.12.2" : null)
                      ?? rootVersionId
                      ?? versionId;
        if (!VersionRegex().IsMatch(gameVersion))
        {
            gameVersion = null;
        }

        if (!hasMainClass)
        {
            return new VersionProfile(
                gameVersion,
                loader,
                DetectedVersionProblem.InvalidManifest,
                "版本 JSON 没有 mainClass，也无法从父版本继承。");
        }

        if (gameVersion is null)
        {
            return new VersionProfile(
                null,
                loader,
                DetectedVersionProblem.InvalidManifest,
                "无法确定 Minecraft 版本。");
        }

        return new VersionProfile(gameVersion, loader, DetectedVersionProblem.None, null);
    }

    private bool TryReadManifest(
        string jsonPath,
        out VersionManifestInfo manifest,
        out string error)
    {
        manifest = VersionManifestInfo.Empty;
        error = string.Empty;

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "版本 JSON 顶层不是对象。";
                return false;
            }

            var libraries = new List<string>();
            if (root.TryGetProperty("libraries", out var libraryArray) &&
                libraryArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in libraryArray.EnumerateArray())
                {
                    if (ReadString(library, "name") is { Length: > 0 } name)
                    {
                        libraries.Add(name);
                    }
                }
            }

            manifest = new VersionManifestInfo(
                ReadString(root, "inheritsFrom"),
                ReadString(root, "clientVersion"),
                ReadString(root, "mainClass"),
                libraries,
                ReadForgeMinecraftVersion(root));
            return true;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "版本 JSON 无法解析：{Path}", jsonPath);
            error = "版本 JSON 无法解析（文件可能损坏或写入中断）。";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"版本文件无法读取：{exception.Message}";
            return false;
        }
    }

    private static string? FirstValidVersion(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && VersionRegex().IsMatch(candidate));

    /// <summary>Forge/NeoForge 合并后的版本 JSON 会在游戏参数中记录 --fml.mcVersion。</summary>
    private static string? ReadForgeMinecraftVersion(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("game", out var game) ||
            game.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? previous = null;
        foreach (var item in game.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (previous == "--fml.mcVersion" && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            previous = value;
        }

        return null;
    }

    private static string DetectLoader(string mainClass, IReadOnlyList<string> libraries)
    {
        bool HasLibrary(string fragment) =>
            libraries.Any(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        if (HasLibrary("net.fabricmc") || mainClass.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (HasLibrary("net.neoforged"))
        {
            return "neoforge";
        }

        if (HasLibrary("cleanroom") || mainClass.Contains("cleanroom", StringComparison.OrdinalIgnoreCase))
        {
            return "cleanroom";
        }

        if (HasLibrary("net.minecraftforge"))
        {
            return "forge";
        }

        return "vanilla";
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _store.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        await _store.DeleteAsync(instanceId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已删除实例 {Name}（{Id}）。", instance.Name, instance.Id);
        return Result.Ok();
    }

    public async Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await _store.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return;
        }

        await _store.SaveAsync(instance with { LastPlayedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateUniqueIdAsync(string name, CancellationToken cancellationToken)
    {
        var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "instance";
        }

        if (slug.Length > 24)
        {
            slug = slug[..24].Trim('-');
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var suffix = RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
            var candidate = $"{slug}-{suffix}";
            if (await _store.GetAsync(candidate, cancellationToken).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        return $"{slug}-{Guid.NewGuid():N}";
    }

    private static string BuildVersionId(string gameVersion, string loader, string? loaderVersion)
    {
        if (string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(loaderVersion))
        {
            return gameVersion;
        }

        var parts = new List<string> { gameVersion, loader.ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(loaderVersion))
        {
            parts.Add(loaderVersion.Trim());
        }

        return string.Join('-', parts);
    }

    [GeneratedRegex("^[0-9A-Za-z._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9A-Za-z._+-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LoaderVersionRegex();

    [GeneratedRegex("^[0-9A-Za-z._-]{1,32}:[0-9A-Za-z._-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OptiFineKeyRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    /// <summary>从单个版本 JSON 中读出的、用于扫描/修复的字段。</summary>
    private sealed record VersionManifestInfo(
        string? InheritsFrom,
        string? ClientVersion,
        string? MainClass,
        IReadOnlyList<string> Libraries,
        string? ForgeMinecraftVersion)
    {
        public static VersionManifestInfo Empty { get; } = new(null, null, null, [], null);
    }

    /// <summary>扫描结果：可导入时为 <see cref="DetectedVersionProblem.None"/>。</summary>
    private sealed record VersionProfile(
        string? GameVersion,
        string Loader,
        DetectedVersionProblem Problem,
        string? ProblemDetail);
}
