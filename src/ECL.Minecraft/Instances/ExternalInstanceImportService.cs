// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Core.Storage.Discovery;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Instances;

/// <summary>外部实例导入选项（F-02 §5.5）。</summary>
public sealed record ExternalImportOptions
{
    public string? DisplayName { get; init; }

    /// <summary>没有身份标记时是否允许只创建 ECL 本地身份（仍不写外部目录）。</summary>
    public bool AllowLocalOnlyIdentity { get; init; } = true;
}

public interface IExternalInstanceImportService
{
    /// <summary>
    /// 把一个发现条目显式导入为 ECL 实例：只写 ECL 本地记录/身份登记，
    /// 不复制、不移动、不改写外部游戏目录。
    /// </summary>
    Task<Result<GameInstance>> ImportAsync(
        string discoveryId,
        ExternalImportOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-02 外部实例显式导入。扫描本身永远不创建可启动实例；导入是唯一创建入口，
/// 失败时回滚本地记录，外部目录保持零写入。
/// </summary>
public sealed partial class ExternalInstanceImportService : IExternalInstanceImportService
{
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    private readonly IInstanceDiscoveryService _discovery;
    private readonly IInstanceStore _store;
    private readonly InstanceIdentityService _identity;
    private readonly IInstanceWriteGate _gate;
    private readonly IAppPaths _paths;
    private readonly ILogger<ExternalInstanceImportService> _logger;

    public ExternalInstanceImportService(
        IInstanceDiscoveryService discovery,
        IInstanceStore store,
        InstanceIdentityService identity,
        IInstanceWriteGate gate,
        IAppPaths paths,
        ILogger<ExternalInstanceImportService>? logger = null)
    {
        _discovery = discovery;
        _store = store;
        _identity = identity;
        _gate = gate;
        _paths = paths;
        _logger = logger ?? NullLogger<ExternalInstanceImportService>.Instance;
    }

    public async Task<Result<GameInstance>> ImportAsync(
        string discoveryId,
        ExternalImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExternalImportOptions();
        if (string.IsNullOrWhiteSpace(discoveryId))
        {
            return Result<GameInstance>.Fail("discovery.invalid_id", "发现条目标识不能为空。");
        }

        var entryResult = await _discovery.GetEntryAsync(discoveryId, cancellationToken).ConfigureAwait(false);
        if (!entryResult.IsSuccess)
        {
            return Result<GameInstance>.Fail(entryResult.Error!);
        }

        var entry = entryResult.Value!;
        var notImportable = ValidateEntry(entry);
        if (notImportable is not null)
        {
            return Result<GameInstance>.Fail(notImportable.Value.Code, notImportable.Value.Message);
        }

        var directory = PathIdentity.Normalize(entry.GameDirectory);
        if (directory is null || !Directory.Exists(directory))
        {
            return Result<GameInstance>.Fail("instance.directory_missing", "发现条目指向的游戏目录不存在。");
        }

        var manifest = ReadManifest(entry, directory);
        if (manifest.ProblemCode is not null)
        {
            return Result<GameInstance>.Fail(
                manifest.ProblemCode,
                "该发现条目的版本清单无效，无法导入。",
                manifest.ProblemDetail);
        }

        var gameVersion = entry.GameVersion ?? manifest.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return Result<GameInstance>.Fail(
                "discovery.invalid_manifest",
                "无法确定该条目的 Minecraft 版本，已拒绝导入。");
        }

        var displayName = string.IsNullOrWhiteSpace(options.DisplayName)
            ? entry.DisplayName
            : options.DisplayName.Trim();
        if (displayName.Length is < 1 or > 48)
        {
            return Result<GameInstance>.Fail("instance.invalid_name", "实例名称需为 1-48 个字符。");
        }

        var all = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var sameDirectory = all.FirstOrDefault(instance =>
            PathsEqual(GetGameDirectory(instance), directory));

        var marker = await _identity.ReadMarkerAsync(directory, cancellationToken).ConfigureAwait(false);
        string? markerInstanceId = null;
        if (marker.State == InstanceMarkerReadState.Invalid)
        {
            return Result<GameInstance>.Fail(
                "discovery.invalid_metadata",
                "该目录的身份标记无效，已拒绝导入。",
                marker.Error);
        }

        if (marker.State == InstanceMarkerReadState.Valid)
        {
            markerInstanceId = marker.Marker!.InstanceId;
            if (sameDirectory is not null &&
                string.Equals(sameDirectory.Id, markerInstanceId, StringComparison.Ordinal))
            {
                return Result<GameInstance>.Fail(
                    "instance.already_imported",
                    "该目录已经作为实例存在，无需重复导入。",
                    sameDirectory.Id);
            }

            var localWithMarker = all.FirstOrDefault(instance =>
                string.Equals(instance.Id, markerInstanceId, StringComparison.Ordinal));
            if (localWithMarker is not null)
            {
                return Result<GameInstance>.Fail(
                    "instance.identity_conflict",
                    "目录标记指向的实例已在本机登记，但位置不同；请先解决身份冲突。",
                    markerInstanceId);
            }

            if (!AppPaths.IsValidInstanceId(markerInstanceId))
            {
                return Result<GameInstance>.Fail(
                    "discovery.identity_conflict",
                    "目录标记中的实例 ID 不合法，已拒绝导入。",
                    markerInstanceId);
            }
        }
        else if (!options.AllowLocalOnlyIdentity)
        {
            return Result<GameInstance>.Fail(
                "discovery.not_importable",
                "该目录没有可验证的身份标记，且调用方不允许创建本地身份。");
        }

        if (sameDirectory is not null)
        {
            return Result<GameInstance>.Fail(
                "instance.already_imported",
                "该游戏目录已经作为实例存在，无需重复导入。",
                sameDirectory.Id);
        }

        if (all.Any(instance => string.Equals(instance.Name, displayName, StringComparison.OrdinalIgnoreCase)))
        {
            return Result<GameInstance>.Fail("instance.duplicate_name", $"已存在同名实例：{displayName}");
        }

        var instanceId = markerInstanceId ?? CreateUniqueId(displayName, all);

        var leaseResult = await _gate.AcquireAsync(instanceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<GameInstance>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = displayName,
            GameVersion = gameVersion,
            VersionId = entry.VersionId ?? manifest.VersionId ?? gameVersion,
            Loader = string.IsNullOrWhiteSpace(entry.Loader) || entry.Loader == "unknown"
                ? manifest.Loader
                : entry.Loader,
            VersionIsolation = entry.Kind == DiscoveryEntryKind.ExternalVersioned,
            GameDirectory = directory,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        var registered = await _identity.EnsureRegisteredAsync(instance, writeMarker: false, cancellationToken)
            .ConfigureAwait(false);
        if (!registered.IsSuccess)
        {
            await RollbackRecordAsync(instance, all, cancellationToken).ConfigureAwait(false);
            return Result<GameInstance>.Fail(registered.Error!);
        }

        var marked = await _discovery.MarkImportedAsync(discoveryId, instance.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!marked.IsSuccess)
        {
            _logger.LogWarning(
                "实例 {InstanceId} 已导入，但发现条目 {DiscoveryId} 未更新导入结果：{Error}",
                instance.Id,
                discoveryId,
                marked.Error);
        }

        _logger.LogInformation(
            "已把外部来源条目 {DiscoveryId} 导入为实例 {Name}（{Id}）；外部目录未写入：{Directory}",
            discoveryId,
            instance.Name,
            instance.Id,
            directory);
        return Result<GameInstance>.Ok(instance);
    }

    private static (string Code, string Message)? ValidateEntry(DiscoveryEntry entry)
    {
        switch (entry.Kind)
        {
            case DiscoveryEntryKind.ManagedForeign:
                return ("discovery.adoption_required", "该条目属于其他 ECL 工作区，需要记录归属迁移，不能直接导入。");
            case DiscoveryEntryKind.SharedRootCandidate:
                return ("discovery.not_importable", "该条目是非隔离多版本共享根候选，需先确认版本隔离，不能直接导入。");
        }

        return entry.Status switch
        {
            DiscoveryEntryStatus.Active => null,
            DiscoveryEntryStatus.PotentialDuplicate => ("discovery.duplicate", "该条目被标记为潜在重复，请先解决重复来源。"),
            DiscoveryEntryStatus.IdentityConflict => ("discovery.identity_conflict", "该条目存在身份冲突，请先解决。"),
            DiscoveryEntryStatus.Invalid => ("discovery.invalid_manifest", "该条目的元数据/版本清单无效，不能导入。"),
            DiscoveryEntryStatus.Missing => ("discovery.not_importable", "该条目在最近扫描中未再出现，不能导入。"),
            DiscoveryEntryStatus.SourceMissing => ("source.missing", "来源目录当前不可访问，不能导入。"),
            DiscoveryEntryStatus.Unbound => ("discovery.not_importable", "来源已解除登记，不能导入。"),
            _ => ("discovery.not_importable", "该条目当前不可导入。"),
        };
    }

    private async Task RollbackRecordAsync(
        GameInstance instance,
        IReadOnlyList<GameInstance> all,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = InstanceDeletionPlanner.Create(
                _paths,
                instance,
                all);
            await _store.DeleteAsync(plan, InstanceDeletionMode.RemoveRecordOnly, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "回滚导入实例 {Id} 的本地记录失败。", instance.Id);
        }
    }

    private static (string? GameVersion, string? VersionId, string Loader, string? ProblemCode, string? ProblemDetail)
        ReadManifest(DiscoveryEntry entry, string directory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.VersionId))
        {
            candidates.Add(Path.Combine(directory, "versions", entry.VersionId + ".json"));
            candidates.Add(Path.Combine(directory, entry.VersionId + ".json"));
        }

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return (entry.GameVersion, entry.VersionId, string.IsNullOrWhiteSpace(entry.Loader) ? "unknown" : entry.Loader,
                entry.GameVersion is null ? "discovery.invalid_manifest" : null,
                entry.GameVersion is null ? "找不到条目对应的版本清单，且发现结果未记录版本。" : null);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxManifestBytes)
            {
                return (null, null, "unknown", "discovery.invalid_manifest", "版本清单超过大小上限。");
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var id = ReadString(root, "id") ?? entry.VersionId;
            var clientVersion = ReadString(root, "clientVersion");
            var inheritsFrom = ReadString(root, "inheritsFrom");
            var loader = DetectLoader(id);
            var gameVersion = clientVersion ?? (loader == "vanilla" ? id : inheritsFrom) ?? inheritsFrom;
            return (gameVersion, id, loader,
                string.IsNullOrWhiteSpace(gameVersion) ? "discovery.invalid_manifest" : null,
                string.IsNullOrWhiteSpace(gameVersion) ? "无法从版本清单确定 Minecraft 版本。" : null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null, "unknown", "discovery.invalid_manifest", $"版本清单读取失败：{exception.Message}");
        }
    }

    private string GetGameDirectory(GameInstance instance) =>
        string.IsNullOrWhiteSpace(instance.GameDirectory)
            ? _paths.GetInstanceGameDirectory(instance.Id)
            : instance.GameDirectory;

    private static string CreateUniqueId(string name, IReadOnlyList<GameInstance> existing)
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
            var candidate = $"{slug}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
            if (existing.All(instance => !string.Equals(instance.Id, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return $"{slug}-{Guid.NewGuid():N}";
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            PathIdentity.Normalize(left),
            PathIdentity.Normalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DetectLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return "unknown";
        }

        if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (versionId.Contains("cleanroom", StringComparison.OrdinalIgnoreCase))
        {
            return "cleanroom";
        }

        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        return "vanilla";
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}
