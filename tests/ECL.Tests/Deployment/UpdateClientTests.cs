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
