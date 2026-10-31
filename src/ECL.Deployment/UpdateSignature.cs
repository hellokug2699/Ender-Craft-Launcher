// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.Deployment;

public sealed record UpdateSignatureEnvelope(
    int SchemaVersion,
    string KeyId,
    string Algorithm,
    string Signature)
{
    public const string RsaPssSha256 = "RSA-PSS-SHA256";
}

/// <summary>
/// 受信任的更新公钥集合。公钥不是秘密，可以随客户端发布；私钥必须留在受控发布环境。
/// 使用 keyId 支持密钥轮换；未知 keyId 一律 fail-closed。
/// </summary>
public sealed class UpdateTrustStore
{
    private readonly IReadOnlyDictionary<string, string> _publicKeys;

    private UpdateTrustStore(IReadOnlyDictionary<string, string> publicKeys) => _publicKeys = publicKeys;

    public static UpdateTrustStore Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    public bool IsEmpty => _publicKeys.Count == 0;

    public IReadOnlyCollection<string> KeyIds => _publicKeys.Keys.ToArray();

    public bool TryGetPublicKey(string keyId, out string publicKey) =>
        _publicKeys.TryGetValue(keyId, out publicKey!);

    public static Result<UpdateTrustStore> FromJson(string json)
    {
        TrustStoreDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TrustStoreDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<UpdateTrustStore>.Fail(
                "update.trust_invalid_json",
                "更新公钥文件不是有效的 JSON。",
                exception.Message);
        }

        if (dto is null || dto.SchemaVersion != 1 || dto.Keys is null)
        {
            return Result<UpdateTrustStore>.Fail(
                "update.trust_invalid",
                "更新公钥文件格式无效。");
        }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in dto.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) ||
                !string.Equals(key.Algorithm, UpdateSignatureEnvelope.RsaPssSha256, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(key.PublicKey) ||
                !keys.TryAdd(key.KeyId, key.PublicKey))
            {
                return Result<UpdateTrustStore>.Fail(
                    "update.trust_invalid",
                    "更新公钥文件包含重复、缺失或不受支持的条目。",
                    key.KeyId);
            }
        }

        return Result<UpdateTrustStore>.Ok(new UpdateTrustStore(keys));
    }

    public static async Task<Result<UpdateTrustStore>> FromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return Result<UpdateTrustStore>.Fail(
                "update.trust_missing",
                "未找到更新公钥文件。",
                path);
        }

        return FromJson(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class TrustStoreDto
    {
        public int SchemaVersion { get; init; }

        public List<TrustKeyDto>? Keys { get; init; }
    }

    private sealed class TrustKeyDto
    {
        public string? KeyId { get; init; }

        public string? Algorithm { get; init; }

        public string? PublicKey { get; init; }
    }
}

public sealed class UpdateSignatureVerifier(UpdateTrustStore trustStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public Result Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureJson)
    {
        UpdateSignatureEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<UpdateSignatureEnvelope>(signatureJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result.Fail(
                "update.signature_invalid_json",
                "更新签名文件不是有效的 JSON。",
                exception.Message);
        }

        if (envelope is null ||
            envelope.SchemaVersion != 1 ||
            !string.Equals(envelope.Algorithm, UpdateSignatureEnvelope.RsaPssSha256, StringComparison.Ordinal))
        {
            return Result.Fail(
                "update.signature_algorithm_unsupported",
                "更新签名格式或算法不受支持。");
        }

        if (!trustStore.TryGetPublicKey(envelope.KeyId, out var publicKey))
        {
            return Result.Fail(
                "update.unknown_key",
                "更新签名使用了未知公钥。",
                envelope.KeyId);
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            return Result.Fail(
                "update.signature_invalid",
                "更新签名不是有效的 Base64。",
                exception.Message);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            return rsa.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)
                ? Result.Ok()
                : Result.Fail("update.signature_invalid", "更新清单签名验证失败。");
        }
        catch (CryptographicException exception)
        {
            return Result.Fail(
                "update.signature_invalid",
                "更新公钥或签名无法验证。",
                exception.Message);
        }
    }
}
