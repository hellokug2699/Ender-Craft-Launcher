// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.Additions;

public sealed record CachedAdditionCatalog(AdditionCatalogPayload Payload, DateTimeOffset FetchedAtUtc);

/// <summary>
/// 本地目录缓存。缓存与签名一起保存，读取时仍重新验证签名；缓存只是离线降级来源，
/// 不被视为最新或可信的替代品（ADR 0032 §8）。
/// </summary>
public sealed class AdditionCatalogStore(string directory)
{
    public const string CatalogFileName = "catalog.json";
    public const string SignatureFileName = "catalog.json.sig";
    public const string MetaFileName = "catalog-meta.json";

    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task<Result> SaveAsync(AdditionCatalogPayload payload, DateTimeOffset fetchedAtUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await WriteAtomicAsync(Path.Combine(Directory, CatalogFileName), payload.CatalogJson, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(Path.Combine(Directory, SignatureFileName), payload.SignatureJson, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(
                    Path.Combine(Directory, MetaFileName),
                    JsonSerializer.Serialize(new CatalogMetaDto { FetchedAtUtc = fetchedAtUtc }, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("additions.catalog_cache_save_failed", "写入扩展目录缓存失败。", exception.Message);
        }
    }

    public async Task<CachedAdditionCatalog?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var catalogPath = Path.Combine(Directory, CatalogFileName);
        var signaturePath = Path.Combine(Directory, SignatureFileName);
        if (!File.Exists(catalogPath) || !File.Exists(signaturePath))
        {
            return null;
        }

        try
        {
            var catalog = await File.ReadAllTextAsync(catalogPath, cancellationToken).ConfigureAwait(false);
            var signature = await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false);
            var metaPath = Path.Combine(Directory, MetaFileName);
            var fetchedAt = File.Exists(metaPath)
                ? JsonSerializer.Deserialize<CatalogMetaDto>(await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false), JsonOptions)?.FetchedAtUtc
                : null;
            return new CachedAdditionCatalog(
                new AdditionCatalogPayload(catalog, signature),
                fetchedAt ?? DateTimeOffset.UnixEpoch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class CatalogMetaDto
    {
        public int SchemaVersion { get; init; } = 1;

        public DateTimeOffset FetchedAtUtc { get; init; }
    }
}

/// <summary>
/// 目录获取与验证。远程不可用、离线或签名失败时保留最近一次通过校验的缓存，并标记为可能过期；
/// 没有可用缓存时向调用方返回明确失败，调用方不得把它升级为启动失败。
/// </summary>
public sealed class AdditionCatalogService(
    IAdditionCatalogSource source,
    AdditionCatalogStore store,
    UpdateTrustStore trustStore,
    TimeProvider? timeProvider = null)
{
    public const string TrustFileName = "additions-trust.json";

    public async Task<Result<AdditionCatalogSnapshot>> GetAsync(
        bool allowCache = true,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var fetched = await source.FetchAsync(cancellationToken).ConfigureAwait(false);
        if (fetched.IsSuccess)
        {
            var verified = Verify(fetched.Value!);
            if (verified.IsSuccess)
            {
                var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                var saved = await store.SaveAsync(fetched.Value!, now, cancellationToken).ConfigureAwait(false);
                if (!saved.IsSuccess)
                {
                    warnings.Add($"目录缓存写入失败：{saved.Error!.Message}");
                }

                return Result<AdditionCatalogSnapshot>.Ok(
                    new AdditionCatalogSnapshot(verified.Value!, FromCache: false, now, warnings));
            }

            warnings.Add($"远程目录验证失败：{verified.Error!.Message}");
        }
        else
        {
            warnings.Add(fetched.Error!.Message);
        }

        if (!allowCache)
        {
            return Result<AdditionCatalogSnapshot>.Fail(
                fetched.IsSuccess ? "additions.catalog_untrusted" : fetched.Error!.Code,
                "无法获取可验证的扩展目录。",
                string.Join("；", warnings));
        }

        var cached = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (cached is null)
        {
            return Result<AdditionCatalogSnapshot>.Fail(
                "additions.catalog_unavailable",
                "扩展目录不可用，且本地没有通过校验的缓存。",
                string.Join("；", warnings));
        }

        var cachedVerified = Verify(cached.Payload);
        if (!cachedVerified.IsSuccess)
        {
            return Result<AdditionCatalogSnapshot>.Fail(
                "additions.catalog_cache_untrusted",
                "本地扩展目录缓存未通过签名校验。",
                cachedVerified.Error!.Message);
        }

        warnings.Add("使用本地缓存的扩展目录；它可能不是最新状态。");
        return Result<AdditionCatalogSnapshot>.Ok(
            new AdditionCatalogSnapshot(cachedVerified.Value!, FromCache: true, cached.FetchedAtUtc, warnings));
    }

    private Result<AdditionCatalog> Verify(AdditionCatalogPayload payload)
    {
        var catalog = AdditionCatalogParser.Parse(payload.CatalogJson);
        if (!catalog.IsSuccess)
        {
            return catalog;
        }

        var verifier = new UpdateSignatureVerifier(trustStore);
        var signature = verifier.Verify(
            Encoding.UTF8.GetBytes(payload.CatalogJson),
            Encoding.UTF8.GetBytes(payload.SignatureJson));
        if (!signature.IsSuccess)
        {
            return Result<AdditionCatalog>.Fail(
                "additions.catalog_signature_invalid",
                "扩展目录签名验证失败，拒绝使用。",
                signature.Error!.Message);
        }

        return catalog;
    }

    public static async Task<Result<UpdateTrustStore>> LoadTrustAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        await UpdateTrustStore.FromFileAsync(path, cancellationToken).ConfigureAwait(false);
}
