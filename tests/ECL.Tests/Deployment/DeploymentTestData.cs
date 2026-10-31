// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Deployment;

namespace Ecl.Tests.Deployment;

internal static class DeploymentTestData
{
    public static UpdateManifest CreateManifest(
        string version,
        string packageFileName,
        long packageSize,
        string packageSha256,
        IReadOnlyList<UpdateFile>? files = null,
        string minimumUpdaterVersion = "1.0.0",
        UpdateChannel channel = UpdateChannel.Stable,
        UpdateArchitecture architecture = UpdateArchitecture.X64) =>
        new(
            1,
            "ECL",
            channel,
            ReleaseVersion.Parse(version),
            "windows",
            architecture,
            ReleaseVersion.Parse(minimumUpdaterVersion),
            new UpdatePackage(packageFileName, packageSize, packageSha256),
            files ?? [],
            [],
            "Test release",
            DateTimeOffset.UtcNow);

    public static byte[] Serialize(UpdateManifest manifest) => UpdateManifestParser.Serialize(manifest);

    public static string CreatePackage(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }

    public static (string PrivateKeyPem, string PublicKeyPem, string TrustJson, string KeyId) CreateKeys()
    {
        const string keyId = "test-key-1";
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var trust = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                keys = new[]
                {
                    new
                    {
                        keyId,
                        algorithm = UpdateSignatureEnvelope.RsaPssSha256,
                        publicKey,
                    },
                },
            });
        return (privateKey, publicKey, trust, keyId);
    }

    public static string Sign(byte[] manifest, string privateKeyPem, string keyId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(
            manifest,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keyId,
            algorithm = UpdateSignatureEnvelope.RsaPssSha256,
            signature = Convert.ToBase64String(signature),
        });
    }

    public static UpdateTrustStore TrustStore(string trustJson) =>
        UpdateTrustStore.FromJson(trustJson).Value ?? throw new InvalidOperationException("无效测试公钥。");
}
