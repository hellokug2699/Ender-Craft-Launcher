// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Identity;

/// <summary>实例内的轻量身份标记（ADR 0046 §3）。只保存跨位置识别所需的字段。</summary>
public sealed record InstanceMarker(
    int SchemaVersion,
    string InstanceId,
    DateTimeOffset CreatedAt,
    string? DisplayName = null);

public enum InstanceMarkerReadState
{
    /// <summary>标记不存在；外部实例或尚未写入。</summary>
    None,

    /// <summary>标记可解析且格式在支持范围内。</summary>
    Valid,

    /// <summary>标记存在但无法解析、超过大小限制或 schema 未知。</summary>
    Invalid,
}

public sealed record InstanceMarkerReadResult(
    InstanceMarkerReadState State,
    InstanceMarker? Marker,
    string? Error)
{
    public static InstanceMarkerReadResult Missing { get; } = new(InstanceMarkerReadState.None, null, null);

    public static InstanceMarkerReadResult Invalid(string error) =>
        new(InstanceMarkerReadState.Invalid, null, error);
}

public enum InstanceRegistrationStatus
{
    /// <summary>路径与标记/本地登记一致。</summary>
    Ok,

    /// <summary>登记过的目录当前不存在或不可访问。</summary>
    Missing,

    /// <summary>实例目录中没有身份标记；身份锚点仅存在于本地登记。</summary>
    LocalOnly,

    /// <summary>目录中的标记与本地登记不一致，写操作必须暂停。</summary>
    Conflict,

    /// <summary>尚未验证。</summary>
    Unverified,
}

/// <summary>
/// 本地管理状态（ADR 0046 §2/§4）。路径是位置，不是主键；同一 InstanceId 只能有一个可写位置。
/// </summary>
public sealed record InstanceRegistration(
    string InstanceId,
    string GameDirectory,
    string DisplayName,
    string GameVersion,
    string Loader,
    string Source,
    string MarkerAnchor,
    DateTimeOffset CreatedAt,
    DateTimeOffset FirstRegisteredAt,
    DateTimeOffset? LastVerifiedAt,
    InstanceRegistrationStatus Status,
    string? Note = null);

public sealed class InstanceRegistryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<InstanceRegistration> Instances { get; set; } = [];
}

/// <summary>身份冲突证据。冲突不是异常：它必须被展示并暂停写操作，而不是静默选择其中一个目录。</summary>
public sealed record InstanceIdentityConflict(
    string InstanceId,
    string GameDirectory,
    string Kind,
    string Detail);

public sealed record InstanceVerification(
    InstanceRegistration Registration,
    InstanceRegistrationStatus Status,
    EvidenceConfidence Confidence,
    string Message);

/// <summary>
/// 稳定实例身份与元数据（ADR 0046）。GameInstance.Id 作为 InstanceId 使用；
/// 本服务负责实例内标记、本地注册表、路径变化验证、克隆/迁移身份语义与冲突检测。
/// </summary>
public sealed class InstanceIdentityService
{
    private const int MaxMarkerBytes = 16 * 1024;
    private const int MaxMarkerDepth = 8;

    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceIdentityService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstanceIdentityService(
        IAppPaths paths,
        ILogger<InstanceIdentityService>? logger = null,
        TimeProvider? time = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceIdentityService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string RegistryPath => Path.Combine(_paths.ConfigDirectory, "instance-engine", "registry.json");

    public static string GetMarkerPath(string gameDirectory) =>
        Path.Combine(gameDirectory, ".ecl", "instance.json");

    public async Task<InstanceMarkerReadResult> ReadMarkerAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = GetMarkerPath(gameDirectory);
        if (!File.Exists(path))
        {
            return InstanceMarkerReadResult.Missing;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxMarkerBytes)
            {
                return InstanceMarkerReadResult.Invalid($"身份标记超过 {MaxMarkerBytes} 字节限制。");
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxMarkerDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InstanceMarkerReadResult.Invalid("身份标记顶层不是对象。");
            }

            var schema = root.TryGetProperty("schemaVersion", out var schemaElement) &&
                         schemaElement.TryGetInt32(out var schemaValue)
                ? schemaValue
                : 0;
            var instanceId = root.TryGetProperty("instanceId", out var idElement) &&
                             idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (schema <= 0 || !AppPaths.IsValidInstanceId(instanceId))
            {
                return InstanceMarkerReadResult.Invalid("身份标记缺少有效的 schemaVersion 或 instanceId。");
            }

            if (schema > InstanceRegistryDocument.CurrentSchemaVersion)
            {
                // 未知的更高版本保守降级：可以识别身份，但不据此扩展任何行为。
                _logger.LogWarning("实例标记 schema {Schema} 高于当前支持版本 {Current}。", schema, InstanceRegistryDocument.CurrentSchemaVersion);
            }

            var createdAt = root.TryGetProperty("createdAt", out var createdElement) &&
                            createdElement.TryGetDateTimeOffset(out var createdValue)
                ? createdValue
                : DateTimeOffset.MinValue;
            var displayName = root.TryGetProperty("displayName", out var nameElement) &&
                              nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            return new InstanceMarkerReadResult(
                InstanceMarkerReadState.Valid,
                new InstanceMarker(schema, instanceId!, createdAt, displayName),
                null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取实例身份标记失败：{Path}", path);
            return InstanceMarkerReadResult.Invalid($"身份标记无法读取：{exception.Message}");
        }
    }

    /// <summary>
    /// 写入实例内标记。已存在不同 InstanceId 的标记时拒绝覆盖；损坏的标记也不会被静默替换。
    /// </summary>
    public async Task<Result> WriteMarkerAsync(
        string gameDirectory,
        string instanceId,
        DateTimeOffset? createdAt = null,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result.Fail("identity.invalid_id", "实例 ID 不合法。");
        }

        var existing = await ReadMarkerAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        switch (existing.State)
        {
            case InstanceMarkerReadState.Valid when
                string.Equals(existing.Marker!.InstanceId, instanceId, StringComparison.Ordinal):
                return Result.Ok();
            case InstanceMarkerReadState.Valid:
                return Result.Fail(
                    "identity.marker_conflict",
                    "实例目录中已有不同的 InstanceId 标记，拒绝覆盖。",
                    $"现有：{existing.Marker!.InstanceId}，请求：{instanceId}");
            case InstanceMarkerReadState.Invalid:
                return Result.Fail(
                    "identity.marker_invalid",
                    "实例目录中的身份标记损坏，拒绝覆盖。",
                    existing.Error);
        }

        var marker = new InstanceMarker(
            InstanceRegistryDocument.CurrentSchemaVersion,
            instanceId,
            createdAt ?? _time.GetUtcNow(),
            displayName);
        var path = GetMarkerPath(gameDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicWriteAllTextAsync(
                path,
                JsonSerializer.Serialize(marker, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "写入实例身份标记失败：{Path}", path);
            return Result.Fail(
                "identity.marker_write_failed",
                "无法写入实例身份标记；实例仍可通过本地登记识别。",
                exception.Message);
        }
    }

    /// <summary>
    /// 登记或更新一个实例身份。写入标记失败不会让管理与启动失败，但会明确记录本地锚点。
    /// </summary>
    public async Task<Result<InstanceRegistration>> EnsureRegisteredAsync(
        GameInstance instance,
        bool writeMarker,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instance.Id))
        {
            return Result<InstanceRegistration>.Fail("identity.invalid_id", "实例 ID 不合法。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        if (!Directory.Exists(gameDirectory))
        {
            return Result<InstanceRegistration>.Fail(
                "identity.directory_missing",
                "实例游戏目录不存在，无法登记身份。",
                gameDirectory);
        }

        var marker = await ReadMarkerAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        if (marker.State == InstanceMarkerReadState.Valid &&
            !string.Equals(marker.Marker!.InstanceId, instance.Id, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "实例 {InstanceId} 的目录包含另一身份标记 {MarkerId}：{Directory}",
                instance.Id,
                marker.Marker.InstanceId,
                gameDirectory);
            return Result<InstanceRegistration>.Fail(
                "identity.marker_conflict",
                "实例目录中的身份标记与实例 ID 不一致，已暂停登记。",
                $"标记：{marker.Marker.InstanceId}，登记：{instance.Id}");
        }

        if (marker.State == InstanceMarkerReadState.Invalid)
        {
            return Result<InstanceRegistration>.Fail(
                "identity.marker_invalid",
                "实例目录中的身份标记损坏，未自动覆盖。",
                marker.Error);
        }

        var markerAnchor = marker.State == InstanceMarkerReadState.Valid ? "instance" : "local";
        if (marker.State == InstanceMarkerReadState.None && writeMarker)
        {
            var written = await WriteMarkerAsync(
                gameDirectory,
                instance.Id,
                instance.CreatedAt,
                instance.Name,
                cancellationToken).ConfigureAwait(false);
            if (written.IsSuccess)
            {
                markerAnchor = "instance";
            }
            else if (written.Error?.Code is not "identity.marker_write_failed")
            {
                return Result<InstanceRegistration>.Fail(written.Error!);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Result<InstanceRegistration>.Fail(error);
            }

            var now = _time.GetUtcNow();
            var existing = document.Instances.FirstOrDefault(item =>
                string.Equals(item.InstanceId, instance.Id, StringComparison.Ordinal));
            var registration = new InstanceRegistration(
                instance.Id,
                InstancePathIdentity.Normalize(gameDirectory),
                instance.Name,
                instance.GameVersion,
                instance.Loader,
                string.IsNullOrWhiteSpace(instance.GameDirectory) ? "ecl-managed" : "external",
                markerAnchor,
                instance.CreatedAt,
                existing?.FirstRegisteredAt ?? now,
                now,
                markerAnchor == "instance" ? InstanceRegistrationStatus.Ok : InstanceRegistrationStatus.LocalOnly);

            if (existing is not null)
            {
                document.Instances.Remove(existing);
            }

            // 同一路径不能登记为两个身份，否则恢复/删除会竞争同一组文件。
            var pathOwner = document.Instances.FirstOrDefault(item =>
                InstancePathIdentity.Equals(item.GameDirectory, gameDirectory) &&
                !string.Equals(item.InstanceId, instance.Id, StringComparison.Ordinal));
            if (pathOwner is not null)
            {
                return Result<InstanceRegistration>.Fail(
                    "identity.path_conflict",
                    "该游戏目录已登记为其他实例，拒绝重复登记。",
                    pathOwner.InstanceId);
            }

            document.Instances.Add(registration);
            document.Instances = document.Instances
                .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                .ToList();
            var saved = await SaveRegistryAsync(document, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return Result<InstanceRegistration>.Fail(saved.Error!);
            }

            return Result<InstanceRegistration>.Ok(registration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<InstanceRegistration>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
        return error is not null
            ? Result<IReadOnlyList<InstanceRegistration>>.Fail(error)
            : Result<IReadOnlyList<InstanceRegistration>>.Ok(document.Instances.ToArray());
    }

    public async Task<Result<InstanceVerification>> VerifyAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceVerification>.Fail("identity.invalid_id", "实例 ID 不合法。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Result<InstanceVerification>.Fail(error);
            }

            var registration = document.Instances.FirstOrDefault(item =>
                string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
            if (registration is null)
            {
                return Result<InstanceVerification>.Fail(
                    "identity.not_registered",
                    "实例尚未登记身份。");
            }

            var now = _time.GetUtcNow();
            if (!Directory.Exists(registration.GameDirectory))
            {
                var missing = registration with
                {
                    Status = InstanceRegistrationStatus.Missing,
                    LastVerifiedAt = now,
                    Note = "游戏目录不存在或当前不可访问。",
                };
                Replace(document, missing);
                await SaveRegistryAsync(document, cancellationToken).ConfigureAwait(false);
                return Result<InstanceVerification>.Ok(new InstanceVerification(
                    missing,
                    InstanceRegistrationStatus.Missing,
                    EvidenceConfidence.Unknown,
                    "游戏目录不存在；身份无法验证。"));
            }

            var marker = await ReadMarkerAsync(registration.GameDirectory, cancellationToken).ConfigureAwait(false);
            InstanceVerification verification;
            switch (marker.State)
            {
                case InstanceMarkerReadState.Valid when
                    string.Equals(marker.Marker!.InstanceId, instanceId, StringComparison.Ordinal):
                    verification = new InstanceVerification(
                        registration with
                        {
                            Status = InstanceRegistrationStatus.Ok,
                            MarkerAnchor = "instance",
                            LastVerifiedAt = now,
                            Note = null,
                        },
                        InstanceRegistrationStatus.Ok,
                        EvidenceConfidence.Confirmed,
                        "实例内标记与本地登记一致。");
                    break;
                case InstanceMarkerReadState.Valid:
                    verification = new InstanceVerification(
                        registration with
                        {
                            Status = InstanceRegistrationStatus.Conflict,
                            LastVerifiedAt = now,
                            Note = $"标记身份为 {marker.Marker!.InstanceId}。",
                        },
                        InstanceRegistrationStatus.Conflict,
                        EvidenceConfidence.Confirmed,
                        "实例目录中的身份标记与本地登记冲突；写操作必须暂停。");
                    break;
                case InstanceMarkerReadState.Invalid:
                    verification = new InstanceVerification(
                        registration with
                        {
                            Status = InstanceRegistrationStatus.Conflict,
                            LastVerifiedAt = now,
                            Note = marker.Error,
                        },
                        InstanceRegistrationStatus.Conflict,
                        EvidenceConfidence.Failed,
                        "实例身份标记损坏，无法确认身份。");
                    break;
                default:
                    verification = new InstanceVerification(
                        registration with
                        {
                            Status = InstanceRegistrationStatus.LocalOnly,
                            LastVerifiedAt = now,
                            Note = "实例目录没有身份标记；身份锚点仅在本地。",
                        },
                        InstanceRegistrationStatus.LocalOnly,
                        EvidenceConfidence.Partial,
                        "未找到实例内身份标记；仅本地登记可用。");
                    break;
            }

            Replace(document, verification.Registration);
            await SaveRegistryAsync(document, cancellationToken).ConfigureAwait(false);
            return Result<InstanceVerification>.Ok(verification);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 重新定位：只更新位置关系，不改变 InstanceId。目标目录必须存在，且其中的标记（若有）必须属于同一实例。
    /// </summary>
    public async Task<Result<InstanceRegistration>> RelocateAsync(
        string instanceId,
        string newGameDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceRegistration>.Fail("identity.invalid_id", "实例 ID 不合法。");
        }

        string normalized;
        try
        {
            normalized = InstancePathIdentity.Normalize(newGameDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<InstanceRegistration>.Fail("identity.invalid_path", "新目录路径不合法。", exception.Message);
        }

        if (!Directory.Exists(normalized))
        {
            return Result<InstanceRegistration>.Fail("identity.directory_missing", "新目录不存在。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Result<InstanceRegistration>.Fail(error);
            }

            var registration = document.Instances.FirstOrDefault(item =>
                string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
            if (registration is null)
            {
                return Result<InstanceRegistration>.Fail("identity.not_registered", "实例尚未登记身份。");
            }

            var occupied = document.Instances.FirstOrDefault(item =>
                !string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal) &&
                InstancePathIdentity.Equals(item.GameDirectory, normalized));
            if (occupied is not null)
            {
                return Result<InstanceRegistration>.Fail(
                    "identity.path_conflict",
                    "目标目录已登记为其他实例。",
                    occupied.InstanceId);
            }

            var marker = await ReadMarkerAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (marker.State == InstanceMarkerReadState.Valid &&
                !string.Equals(marker.Marker!.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return Result<InstanceRegistration>.Fail(
                    "identity.marker_conflict",
                    "目标目录中的身份标记属于其他实例。",
                    marker.Marker.InstanceId);
            }

            if (marker.State != InstanceMarkerReadState.Valid &&
                string.Equals(registration.MarkerAnchor, "instance", StringComparison.Ordinal))
            {
                return Result<InstanceRegistration>.Fail(
                    "identity.marker_missing",
                    "原实例使用实例内标记，但目标目录没有可验证的身份标记；拒绝自动重新关联。");
            }

            var updated = registration with
            {
                GameDirectory = normalized,
                MarkerAnchor = marker.State == InstanceMarkerReadState.Valid ? "instance" : "local",
                Status = marker.State == InstanceMarkerReadState.Valid
                    ? InstanceRegistrationStatus.Ok
                    : InstanceRegistrationStatus.LocalOnly,
                LastVerifiedAt = _time.GetUtcNow(),
                Note = null,
            };
            Replace(document, updated);
            var saved = await SaveRegistryAsync(document, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return Result<InstanceRegistration>.Fail(saved.Error!);
            }

            _logger.LogInformation("实例 {InstanceId} 已重新定位到 {Directory}。", instanceId, normalized);
            return Result<InstanceRegistration>.Ok(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>移除本地登记（不删除实例标记或文件）。</summary>
    public async Task<Result> UnregisterAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Result.Fail(error);
            }

            var removed = document.Instances.RemoveAll(item =>
                string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal));
            return removed == 0 ? Result.Ok() : await SaveRegistryAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 检查登记与目录标记之间的一致性问题：标记属于其他身份、同一身份出现在多个目录、目录中存在未登记标记。
    /// 本方法只报告证据，不修改任何身份。
    /// </summary>
    public async Task<Result<IReadOnlyList<InstanceIdentityConflict>>> FindConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        var (document, error) = await LoadRegistryAsync(cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return Result<IReadOnlyList<InstanceIdentityConflict>>.Fail(error);
        }

        var conflicts = new List<InstanceIdentityConflict>();
        var byPath = new Dictionary<string, string>(InstancePathIdentity.Comparer);

        foreach (var registration in document.Instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPath.TryAdd(InstancePathIdentity.Normalize(registration.GameDirectory), registration.InstanceId))
            {
                conflicts.Add(new InstanceIdentityConflict(
                    registration.InstanceId,
                    registration.GameDirectory,
                    "duplicate-path",
                    "同一路径被多个本地登记引用。"));
            }

            if (!Directory.Exists(registration.GameDirectory))
            {
                continue;
            }

            var marker = await ReadMarkerAsync(registration.GameDirectory, cancellationToken).ConfigureAwait(false);
            if (marker.State == InstanceMarkerReadState.Valid &&
                !string.Equals(marker.Marker!.InstanceId, registration.InstanceId, StringComparison.Ordinal))
            {
                conflicts.Add(new InstanceIdentityConflict(
                    registration.InstanceId,
                    registration.GameDirectory,
                    "marker-mismatch",
                    $"目录标记属于 {marker.Marker.InstanceId}。"));
            }
        }

        // 同一 InstanceId 的多个目录不能同时作为可写实例；这里只按登记路径检查，
        // 不扫描整块磁盘，避免把备份/无关目录误判为实例。
        foreach (var group in document.Instances.GroupBy(item => item.InstanceId, StringComparer.Ordinal))
        {
            var paths = group
                .Select(item => InstancePathIdentity.Normalize(item.GameDirectory))
                .Distinct(InstancePathIdentity.Comparer)
                .ToArray();
            if (paths.Length > 1)
            {
                foreach (var path in paths)
                {
                    conflicts.Add(new InstanceIdentityConflict(
                        group.Key,
                        path,
                        "duplicate-instance",
                        "同一 InstanceId 登记在多个目录；只允许一个可写位置。"));
                }
            }
        }

        return Result<IReadOnlyList<InstanceIdentityConflict>>.Ok(conflicts);
    }

    private static void Replace(InstanceRegistryDocument document, InstanceRegistration registration)
    {
        document.Instances.RemoveAll(item =>
            string.Equals(item.InstanceId, registration.InstanceId, StringComparison.Ordinal));
        document.Instances.Add(registration);
        document.Instances = document.Instances
            .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<(InstanceRegistryDocument Document, EclError? Error)> LoadRegistryAsync(
        CancellationToken cancellationToken)
    {
        var path = RegistryPath;
        if (!File.Exists(path))
        {
            return (new InstanceRegistryDocument(), null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<InstanceRegistryDocument>(json, EclJson.Default)
                           ?? new InstanceRegistryDocument();
            document.Instances ??= [];
            if (document.SchemaVersion > InstanceRegistryDocument.CurrentSchemaVersion)
            {
                return (document, new EclError(
                    "identity.registry_unsupported",
                    "本地实例登记使用更高 schema，当前版本拒绝读改写。"));
            }

            return (document, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "读取实例登记失败：{Path}", path);
            return (new InstanceRegistryDocument(), new EclError(
                "identity.registry_corrupt",
                "本地实例登记损坏，已拒绝覆盖；请先修复或备份该文件。",
                exception.Message));
        }
    }

    private async Task<Result> SaveRegistryAsync(
        InstanceRegistryDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            document.SchemaVersion = InstanceRegistryDocument.CurrentSchemaVersion;
            await AtomicWriteAllTextAsync(
                RegistryPath,
                JsonSerializer.Serialize(document, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "写入实例登记失败：{Path}", RegistryPath);
            return Result.Fail(
                "identity.registry_write_failed",
                "无法写入本地实例登记。",
                exception.Message);
        }
    }

    private static async Task AtomicWriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 残留临时文件不影响下一次原子写入。
                }
            }
        }
    }
}

/// <summary>路径规范化与比较。Windows 上大小写不敏感，其它平台按序数比较。</summary>
public static class InstancePathIdentity
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Equals(string left, string right) =>
        Comparer.Equals(Normalize(left), Normalize(right));

    public static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
