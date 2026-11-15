// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateClientTests
{
    [Fact]
    public async Task ChecksSignedManifestAndDownloadsVerifiedPackage()
    {
        using var environment = new TempDirectory();
        var packagePath = environment.Combine("ECL-1.0.1.zip");
        DeploymentTestData.CreatePackage(
            packagePath,
            new Dictionary<string, byte[]> { ["ECL.exe"] = Encoding.UTF8.GetBytes("payload") });
        var packageBytes = await File.ReadAllBytesAsync(packagePath);
        var executableBytes = Encoding.UTF8.GetBytes("payload");
        var manifest = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL-1.0.1.zip",
            packageBytes.Length,
            UpdateHash.ComputeSha256(packageBytes),
            [new UpdateFile("ECL.exe", executableBytes.Length, UpdateHash.ComputeSha256(executableBytes))]);
        var manifestBytes = DeploymentTestData.Serialize(manifest);
        var keys = DeploymentTestData.CreateKeys();
        var signatureBytes = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(manifestBytes, keys.PrivateKeyPem, keys.KeyId));
        using var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            return uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? Bytes(signatureBytes)
                : uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                    ? Bytes(manifestBytes)
                    : Bytes(packageBytes);
        });
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));

        var check = await client.CheckAsync(new Uri("https://updates.example.com/ecl/"), UpdateChannel.Stable);

        Assert.True(check.IsSuccess, check.Error?.ToString());
        var destination = environment.Combine("download", "package.zip");
        var download = await client.DownloadPackageAsync(check.Value!.Manifest, check.Value.PackageUri, destination);
        Assert.True(download.IsSuccess, download.Error?.ToString());
        Assert.Equal(packageBytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ResolvesAbsoluteImmutablePackageUrlWithoutJoiningFeedPath()
    {
        var keys = DeploymentTestData.CreateKeys();
        var digest = new string('a', 64);
        var packageUrl = $"https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/nightly-packages/ECL-1.0.1-win-x64-{digest}.zip";
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "unused.zip", 1, digest) with
        {
            Channel = UpdateChannel.Nightly,
            Package = new UpdatePackage(packageUrl, 1, digest),
        };
        var manifestBytes = DeploymentTestData.Serialize(manifest);
        var signatureBytes = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(manifestBytes, keys.PrivateKeyPem, keys.KeyId));
        using var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? Bytes(signatureBytes)
                : Bytes(manifestBytes));
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));

        var result = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Nightly);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(new Uri(packageUrl), result.Value!.PackageUri);
    }

    [Fact]
    public async Task RejectsFeedRedirectWithoutFollowingIt()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/collect") },
        });
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(UpdateTrustStore.Empty));

        var result = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Nightly);

        Assert.Equal("update.feed_redirect_rejected", result.Error?.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FollowsOnlyAllowlistedGitHubReleaseAssetRedirects()
    {
        var keys = DeploymentTestData.CreateKeys();
        var digest = new string('a', 64);
        var packageUrl = $"https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/nightly-packages/ECL-1.0.1-win-x64-{digest}.zip";
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "unused.zip", 1, digest) with
        {
            Channel = UpdateChannel.Nightly,
            Package = new UpdatePackage(packageUrl, 1, digest),
        };
        var manifestBytes = DeploymentTestData.Serialize(manifest);
        var signatureBytes = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(manifestBytes, keys.PrivateKeyPem, keys.KeyId));
        var payload = Encoding.UTF8.GetBytes("x");
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)) return Bytes(signatureBytes);
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)) return Bytes(manifestBytes);
            calls++;
            if (calls == 1)
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset") },
                };
            return Bytes(payload);
        });
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));
        var check = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Nightly);
        Assert.True(check.IsSuccess, check.Error?.ToString());

        using var environment = new TempDirectory();
        var result = await client.DownloadPackageAsync(check.Value!.Manifest, check.Value.PackageUri, environment.Combine("package.zip"));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RejectsGitHubReleaseAssetRedirectToUntrustedHost()
    {
        var keys = DeploymentTestData.CreateKeys();
        var digest = new string('a', 64);
        var packageUrl = $"https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/nightly-packages/ECL-1.0.1-win-x64-{digest}.zip";
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "unused.zip", 1, digest) with
        {
            Channel = UpdateChannel.Nightly,
            Package = new UpdatePackage(packageUrl, 1, digest),
        };
        var manifestBytes = DeploymentTestData.Serialize(manifest);
        var signatureBytes = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(manifestBytes, keys.PrivateKeyPem, keys.KeyId));
        using var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)) return Bytes(signatureBytes);
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)) return Bytes(manifestBytes);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://attacker.example/asset") },
            };
        });
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));
        var check = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Nightly);
        Assert.True(check.IsSuccess, check.Error?.ToString());

        using var environment = new TempDirectory();
        var result = await client.DownloadPackageAsync(check.Value!.Manifest, check.Value.PackageUri, environment.Combine("package.zip"));
        Assert.Equal("update.package_download_failed", result.Error?.Code);
    }

    [Fact]
    public async Task RetriesSignedFeedPairWhenDeploymentChangesBetweenRequests()
    {
        var keys = DeploymentTestData.CreateKeys();
        var oldManifest = DeploymentTestData.CreateManifest("1.0.0", "old.zip", 1, new string('a', 64));
        var newManifest = DeploymentTestData.CreateManifest("1.0.1", "new.zip", 1, new string('b', 64));
        var oldBytes = DeploymentTestData.Serialize(oldManifest);
        var newBytes = DeploymentTestData.Serialize(newManifest);
        var oldSignature = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(oldBytes, keys.PrivateKeyPem, keys.KeyId));
        var newSignature = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(newBytes, keys.PrivateKeyPem, keys.KeyId));
        var signatureRequests = 0;
        using var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal))
            {
                signatureRequests++;
                return Bytes(signatureRequests == 1 ? oldSignature : newSignature);
            }

            return Bytes(newBytes);
        });
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));

        var result = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Stable);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("1.0.1", result.Value!.Manifest.Version.ToString());
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task RejectsManifestFromAnotherChannel()
    {
        var keys = DeploymentTestData.CreateKeys();
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, new string('a', 64));
        var manifestBytes = DeploymentTestData.Serialize(manifest);
        var signatureBytes = Encoding.UTF8.GetBytes(
            DeploymentTestData.Sign(manifestBytes, keys.PrivateKeyPem, keys.KeyId));
        using var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? Bytes(signatureBytes)
                : Bytes(manifestBytes));
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(DeploymentTestData.TrustStore(keys.TrustJson)));

        var result = await client.CheckAsync(new Uri("https://updates.endercraftlauncher.top/"), UpdateChannel.Nightly);

        Assert.Equal("update.channel_mismatch", result.Error?.Code);
    }

    [Fact]
    public async Task RejectsInsecureFeed()
    {
        using var handler = new StubHandler(_ => Bytes([]));
        using var http = new HttpClient(handler);
        var client = new UpdateClient(http, new UpdateSignatureVerifier(UpdateTrustStore.Empty));

        var result = await client.CheckAsync(new Uri("http://updates.example.com/"), UpdateChannel.Stable);

        Assert.Equal("update.insecure_feed", result.Error?.Code);
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Bytes(byte[] content) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-deploy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不影响结果。
            }
        }
    }
}
