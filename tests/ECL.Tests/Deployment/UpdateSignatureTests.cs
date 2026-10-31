// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateSignatureTests
{
    [Fact]
    public void VerifiesSignedManifestWithTrustedKey()
    {
        var keys = DeploymentTestData.CreateKeys();
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, new string('a', 64));
        var bytes = DeploymentTestData.Serialize(manifest);
        var signature = DeploymentTestData.Sign(bytes, keys.PrivateKeyPem, keys.KeyId);
        var verifier = new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson));

        Assert.True(verifier.Verify(bytes, Encoding.UTF8.GetBytes(signature)).IsSuccess);
    }

    [Fact]
    public void RejectsTamperedManifestAndUnknownKey()
    {
        var keys = DeploymentTestData.CreateKeys();
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, new string('a', 64));
        var bytes = DeploymentTestData.Serialize(manifest);
        var signature = DeploymentTestData.Sign(bytes, keys.PrivateKeyPem, keys.KeyId);
        var verifier = new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson));

        var tampered = bytes.ToArray();
        tampered[^2] ^= 0xFF;
        Assert.Equal(
            "update.signature_invalid",
            verifier.Verify(tampered, Encoding.UTF8.GetBytes(signature)).Error?.Code);

        var otherKeys = DeploymentTestData.CreateKeys();
        Assert.Equal(
            "update.unknown_key",
            verifier.Verify(bytes, Encoding.UTF8.GetBytes(
                DeploymentTestData.Sign(bytes, otherKeys.PrivateKeyPem, "unknown"))).Error?.Code);
    }

    [Fact]
    public void RejectsUnsupportedAlgorithmAndMalformedEnvelope()
    {
        var store = UpdateTrustStore.Empty;
        var verifier = new UpdateSignatureVerifier(store);
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, new string('a', 64));
        var bytes = DeploymentTestData.Serialize(manifest);
        var envelope = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keyId = "k",
            algorithm = "RSA-PKCS1-SHA256",
            signature = "AAAA",
        });

        Assert.Equal(
            "update.signature_algorithm_unsupported",
            verifier.Verify(bytes, Encoding.UTF8.GetBytes(envelope)).Error?.Code);
        Assert.Equal(
            "update.signature_invalid_json",
            verifier.Verify(bytes, "not json"u8).Error?.Code);
    }
}
