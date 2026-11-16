// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Content;

/// <summary>
/// 实例级内容更新策略（F-06 §5；维护者决策）：Allow 允许更新、NotifyOnly 只提示、Block 禁止更新。
/// Block 是领域结论，CLI/批量/后台都必须读取同一策略，不得由调用方自行“确认绕过”。
/// </summary>
public enum InstanceContentUpdatePolicy
{
    Allow = 0,
    NotifyOnly = 1,
    Block = 2,
}

public static class InstanceContentUpdatePolicyKeys
{
    public const string Key = "instance.content.updatePolicy";
}

public interface IInstanceContentUpdatePolicyService
{
    Task<Result<InstanceContentUpdatePolicy>> GetAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result> SetAsync(
        string instanceId,
        InstanceContentUpdatePolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 更新策略持久化在实例设置（<c>instances/&lt;id&gt;/settings.json</c>）的同一配置键，
/// 与设置页/CLI 共享；读取时非法值回退 <see cref="InstanceContentUpdatePolicy.Allow"/> 并记录警告，
/// 不把损坏值解释成允许更新。
/// </summary>
public sealed class InstanceContentUpdatePolicyService : IInstanceContentUpdatePolicyService
{
    private readonly IInstanceService _instances;
    private readonly ISettingsService _settings;
    private readonly ILogger<InstanceContentUpdatePolicyService> _logger;

    public InstanceContentUpdatePolicyService(
        IInstanceService instances,
        ISettingsService settings,
        ILogger<InstanceContentUpdatePolicyService>? logger = null)
    {
        _instances = instances;
        _settings = settings;
        _logger = logger ?? NullLogger<InstanceContentUpdatePolicyService>.Instance;
    }

    public async Task<Result<InstanceContentUpdatePolicy>> GetAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result<InstanceContentUpdatePolicy>.Fail("instance.not_found", "实例不存在。");
        }

        var raw = _settings.GetRawString(InstanceContentUpdatePolicyKeys.Key, instanceId);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result<InstanceContentUpdatePolicy>.Ok(InstanceContentUpdatePolicy.Allow);
        }

        if (TryParse(raw, out var policy))
        {
            return Result<InstanceContentUpdatePolicy>.Ok(policy);
        }

        _logger.LogWarning(
            "实例 {InstanceId} 的更新策略值 {Value} 非法，按默认 Allow 处理。",
            instanceId,
            raw);
        return Result<InstanceContentUpdatePolicy>.Ok(InstanceContentUpdatePolicy.Allow);
    }

    public async Task<Result> SetAsync(
        string instanceId,
        InstanceContentUpdatePolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        var value = ToRaw(policy);
        return _settings.TrySetRaw(InstanceContentUpdatePolicyKeys.Key, value, instanceId, out var error)
            ? Result.Ok()
            : Result.Fail("content.policy.write_failed", "更新策略未写入。", error);
    }

    public static string ToRaw(InstanceContentUpdatePolicy policy) => policy switch
    {
        InstanceContentUpdatePolicy.Allow => "allow",
        InstanceContentUpdatePolicy.NotifyOnly => "notify",
        InstanceContentUpdatePolicy.Block => "block",
        _ => "allow",
    };

    public static bool TryParse(string value, out InstanceContentUpdatePolicy policy)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "allow":
            case "allowed":
                policy = InstanceContentUpdatePolicy.Allow;
                return true;
            case "notify":
            case "notify-only":
            case "notifyonly":
                policy = InstanceContentUpdatePolicy.NotifyOnly;
                return true;
            case "block":
            case "blocked":
                policy = InstanceContentUpdatePolicy.Block;
                return true;
            default:
                policy = InstanceContentUpdatePolicy.Allow;
                return false;
        }
    }
}

/// <summary>可证明来源的已装内容记录（当前仅有 CurseForge 记录提供稳定 ProjectId/FileId）。</summary>
public sealed record InstalledContentRecord(
    ContentSourceKind Source,
    string ProjectId,
    string FileId,
    string FileName,
    DateTimeOffset? InstalledAt);

public sealed record ContentUpdateCandidate(
    InstalledContentRecord Installed,
    string LatestVersionId,
    string? LatestVersionNumber,
    string? LatestFileName,
    string? LatestDownloadUrl,
    bool Allowed,
    string Reason);

public sealed record ContentUpdateCheckResult(
    string InstanceId,
    InstanceContentUpdatePolicy Policy,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ContentUpdateCandidate> Candidates,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Skipped)
{
    public int AvailableCount => Candidates.Count(candidate => candidate.Allowed);

    public bool UpdatesBlocked => Policy == InstanceContentUpdatePolicy.Block;
}

public interface IContentUpdateService
{
    /// <summary>
    /// 检查实例可证明来源内容的更新。只返回事实与策略结论；不下载、不替换、不自动应用。
    /// </summary>
    Task<Result<ContentUpdateCheckResult>> CheckAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-06 S3 更新发现：读取 CurseForge 安装记录（唯一可证明的稳定来源），按实例版本/加载器查候选文件，
/// 再按实例策略给出 Allowed/Reason。所有权不可证明的文件只报告为 Skipped，不猜测来源。
/// </summary>
public sealed class ContentUpdateService : IContentUpdateService
{
    private const string CurseForgeStateFile = ".ecl-curseforge.json";

    private readonly IInstanceService _instances;
    private readonly IInstanceContentUpdatePolicyService _policy;
    private readonly Ecl.Minecraft.CurseForge.ICurseForgeClient _curseForge;
    private readonly IAppPaths _paths;
    private readonly ILogger<ContentUpdateService> _logger;
    private readonly TimeProvider _time;

    public ContentUpdateService(
        IInstanceService instances,
        IInstanceContentUpdatePolicyService policy,
        Ecl.Minecraft.CurseForge.ICurseForgeClient curseForge,
        IAppPaths paths,
        ILogger<ContentUpdateService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _policy = policy;
        _curseForge = curseForge;
        _paths = paths;
        _logger = logger ?? NullLogger<ContentUpdateService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<ContentUpdateCheckResult>> CheckAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentUpdateCheckResult>.Fail("instance.not_found", "实例不存在。");
        }

        var policyResult = await _policy.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!policyResult.IsSuccess)
        {
            return Result<ContentUpdateCheckResult>.Fail(policyResult.Error!);
        }

        var policy = policyResult.Value;
        var warnings = new List<string>();
        var skipped = new List<string>();
        var candidates = new List<ContentUpdateCandidate>();

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var installed = LoadInstalled(gameDirectory, warnings);
        if (policy == InstanceContentUpdatePolicy.Block)
        {
            // Block 仍报告“发现到新版本”为不可执行，避免 UI 用其它入口绕过。
            foreach (var record in installed)
            {
                var latest = await FindLatestAsync(instance, record, cancellationToken).ConfigureAwait(false);
                if (latest is not null && latest.Id != record.FileId)
                {
                    candidates.Add(new ContentUpdateCandidate(
                        record,
                        latest.Id,
                        latest.DisplayName,
                        latest.FileName,
                        null,
                        Allowed: false,
                        Reason: "policy.block"));
                }
            }

            return Result<ContentUpdateCheckResult>.Ok(new ContentUpdateCheckResult(
                instanceId,
                policy,
                _time.GetUtcNow(),
                candidates,
                warnings,
                skipped));
        }

        foreach (var record in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await FindLatestAsync(instance, record, cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                skipped.Add($"{record.FileName}：没有与实例版本/加载器兼容的候选文件。");
                continue;
            }

            if (string.Equals(latest.Id, record.FileId, StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add(new ContentUpdateCandidate(
                record,
                latest.Id,
                latest.DisplayName,
                latest.FileName,
                null,
                Allowed: policy == InstanceContentUpdatePolicy.Allow,
                Reason: policy == InstanceContentUpdatePolicy.Allow ? "policy.allow" : "policy.notify"));
        }

        return Result<ContentUpdateCheckResult>.Ok(new ContentUpdateCheckResult(
            instanceId,
            policy,
            _time.GetUtcNow(),
            candidates,
            warnings,
            skipped));
    }

    private async Task<CurseForgeCandidate?> FindLatestAsync(
        GameInstance instance,
        InstalledContentRecord record,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(record.ProjectId, out var modId))
        {
            return null;
        }

        var loader = ParseLoader(instance.Loader);
        var files = await _curseForge
            .GetFilesForModAsync(modId, instance.GameVersion, loader, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!files.IsSuccess || files.Value is null || files.Value.Count == 0)
        {
            return null;
        }

        var latest = files.Value
            .OrderByDescending(file => file.FileDate ?? DateTimeOffset.MinValue)
            .First();
        return new CurseForgeCandidate(
            latest.Id.ToString(),
            latest.DisplayName,
            latest.FileName);
    }

    private static Ecl.Minecraft.CurseForge.CurseForgeModLoader? ParseLoader(string loader) =>
        loader.Trim().ToLowerInvariant() switch
        {
            "fabric" => Ecl.Minecraft.CurseForge.CurseForgeModLoader.Fabric,
            "forge" => Ecl.Minecraft.CurseForge.CurseForgeModLoader.Forge,
            "cleanroom" => Ecl.Minecraft.CurseForge.CurseForgeModLoader.Forge,
            "neoforge" => Ecl.Minecraft.CurseForge.CurseForgeModLoader.NeoForge,
            "quilt" => Ecl.Minecraft.CurseForge.CurseForgeModLoader.Quilt,
            _ => null,
        };

    private static List<InstalledContentRecord> LoadInstalled(string gameDirectory, List<string> warnings)
    {
        var path = Path.Combine(gameDirectory, CurseForgeStateFile);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("mods", out var mods) ||
                mods.ValueKind != JsonValueKind.Array)
            {
                // EclJson 落盘为 camelCase；同时兼容旧 PascalCase。
                if (!document.RootElement.TryGetProperty("Mods", out mods) || mods.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }
            }

            var records = new List<InstalledContentRecord>();
            foreach (var mod in mods.EnumerateArray())
            {
                if (!TryGetString(mod, "fileName", out var fileName) ||
                    !TryGetNumber(mod, "projectId", out var projectId) ||
                    !TryGetNumber(mod, "fileId", out var fileId))
                {
                    continue;
                }

                records.Add(new InstalledContentRecord(
                    ContentSourceKind.CurseForge,
                    projectId,
                    fileId,
                    fileName,
                    null));
            }

            return records;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add("CurseForge 安装记录无法读取；更新检查结果不完整。");
            return [];
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return value.Length > 0;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetNumber(JsonElement element, string name, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt64(out var number) => number.ToString(),
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                _ => string.Empty,
            };
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    private sealed record CurseForgeCandidate(string Id, string? DisplayName, string? FileName);
}