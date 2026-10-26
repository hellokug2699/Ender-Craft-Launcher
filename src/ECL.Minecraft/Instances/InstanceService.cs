// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Instances;

public sealed partial class InstanceService : IInstanceService
{
    private static readonly HashSet<string> SupportedLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "vanilla",
        "forge",
        "neoforge",
        "fabric",
        "quilt",
    };

    private readonly IInstanceStore _store;
    private readonly ILogger<InstanceService> _logger;

    public InstanceService(IInstanceStore store, ILogger<InstanceService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
        _store.ListAsync(cancellationToken);

    public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _store.GetAsync(instanceId, cancellationToken);

    public async Task<Result<GameInstance>> CreateAsync(
        string name,
        string gameVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        gameVersion = gameVersion.Trim();
        loader = loader.Trim();

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

        if (!string.IsNullOrWhiteSpace(loaderVersion) && !VersionRegex().IsMatch(loaderVersion.Trim()))
        {
            return Result<GameInstance>.Fail("instance.invalid_loader_version", "加载器版本格式不正确。");
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
            LoaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("已创建实例 {Name}（{Id}，版本 {VersionId}）。", instance.Name, instance.Id, instance.VersionId);
        return Result<GameInstance>.Ok(instance);
    }

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

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}
