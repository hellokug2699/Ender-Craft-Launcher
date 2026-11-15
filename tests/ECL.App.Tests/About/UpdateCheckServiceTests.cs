// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.Deployment;
using Ecl.Download;
using Xunit;

namespace Ecl.App.Tests.About;

/// <summary>
/// 关于页的只读更新检查（ADR 0051 / E3-1）：先核对 HTTPS 更新源与信任公钥，
/// 再验证签名清单并与当前部署版本比较；不下载更新包、不回退到不安全地址。
/// </summary>
public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task DisabledCheckDoesNotTouchTheNetwork()
    {
        var settings = new FakeSettingsService();
        settings.Values["launcher.checkUpdates"] = "false";
        settings.Values["launcher.updateChannel"] = "nightly";
        var http = new CountingHttpProvider();

        var report = await CreateService(settings, http, new UpdateCheckEnvironment(Path.GetTempPath(), null))
            .CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Disabled, report.Outcome);
        Assert.Equal("nightly", report.Channel);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task MissingFeedIsReportedAsUnavailableBeforeAnyRequest()
    {
        var settings = Enabled(settings: new FakeSettingsService());
        var http = new CountingHttpProvider();

        var report = await CreateService(settings, http, new UpdateCheckEnvironment(Path.GetTempPath(), null))
            .CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Unavailable, report.Outcome);
        Assert.Equal("update.feed_not_configured", report.ErrorCode);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task InsecureFeedIsRejected()
    {
        var settings = Enabled(new FakeSettingsService());
        var http = new CountingHttpProvider();

        var report = await CreateService(
                settings,
                http,
                new UpdateCheckEnvironment(Path.GetTempPath(), "http://example.test/feed/"))
            .CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Unavailable, report.Outcome);
        Assert.Equal("update.insecure_feed", report.ErrorCode);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task MissingTrustStoreIsReportedAsUnavailable()
    {
        var settings = Enabled(new FakeSettingsService());
        var http = new CountingHttpProvider();
        var directory = Directory.CreateTempSubdirectory("ecl-update-missing-trust");
        try
        {
            var report = await CreateService(
                    settings,
                    http,
                    new UpdateCheckEnvironment(directory.FullName, "https://example.test/feed/"))
                .CheckAsync();

            Assert.Equal(UpdateCheckOutcome.Unavailable, report.Outcome);
            Assert.Equal("update.trust_missing", report.ErrorCode);
            Assert.Equal(0, http.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SignedManifestNewerThanTheCurrentVersionIsAnAvailableUpdate()
    {
        var settings = Enabled(new FakeSettingsService(), channel: "dev");
        var keys = CreateKeys();
        var directory = Directory.CreateTempSubdirectory("ecl-update-available");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "update-trust.json"), keys.TrustJson);
            File.WriteAllText(Path.Combine(directory.FullName, "current.json"), """{"version":"0.8.0"}""");
            var manifest = CreateManifest("0.9.0", UpdateChannel.Dev);
            var bytes = UpdateManifestParser.Serialize(manifest);
            var signature = Sign(bytes, keys.PrivateKeyPem, keys.KeyId);
            var http = new CountingHttpProvider(Respond(bytes, Encoding.UTF8.GetBytes(signature)));

            var report = await CreateService(
                    settings,
                    http,
                    new UpdateCheckEnvironment(directory.FullName, "https://example.test/feed/"))
                .CheckAsync();

            Assert.Equal(UpdateCheckOutcome.UpdateAvailable, report.Outcome);
            Assert.Equal("dev", report.Channel);
            Assert.Equal("0.8.0", report.CurrentVersion);
            Assert.Equal("0.9.0", report.AvailableVersion);
            Assert.Equal("Test release", report.ReleaseNotes);
            Assert.Null(report.ErrorCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SignedManifestEqualToTheCurrentVersionIsUpToDate()
    {
        var settings = Enabled(new FakeSettingsService());
        var keys = CreateKeys();
        var directory = Directory.CreateTempSubdirectory("ecl-update-current");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "update-trust.json"), keys.TrustJson);
            File.WriteAllText(Path.Combine(directory.FullName, "current.json"), """{"version":"0.9.0"}""");
            var manifest = CreateManifest("0.9.0", UpdateChannel.Stable);
            var bytes = UpdateManifestParser.Serialize(manifest);
            var signature = Sign(bytes, keys.PrivateKeyPem, keys.KeyId);
            var http = new CountingHttpProvider(Respond(bytes, Encoding.UTF8.GetBytes(signature)));

            var report = await CreateService(
                    settings,
                    http,
                    new UpdateCheckEnvironment(directory.FullName, "https://example.test/feed/"))
                .CheckAsync();

            Assert.Equal(UpdateCheckOutcome.UpToDate, report.Outcome);
            Assert.Equal("0.9.0", report.CurrentVersion);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SignedManifestForAnotherKeyFailsTheCheck()
    {
        var settings = Enabled(new FakeSettingsService());
        var trusted = CreateKeys();
        var attacker = CreateKeys();
        var directory = Directory.CreateTempSubdirectory("ecl-update-bad-signature");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "update-trust.json"), trusted.TrustJson);
            File.WriteAllText(Path.Combine(directory.FullName, "current.json"), """{"version":"0.8.0"}""");
            var bytes = UpdateManifestParser.Serialize(CreateManifest("0.9.0", UpdateChannel.Stable));
            // 用未受信任的私钥签名：检查必须失败，而不是把清单当作可信结果。
            var signature = Sign(bytes, attacker.PrivateKeyPem, trusted.KeyId);
            var http = new CountingHttpProvider(Respond(bytes, Encoding.UTF8.GetBytes(signature)));

            var report = await CreateService(
                    settings,
                    http,
                    new UpdateCheckEnvironment(directory.FullName, "https://example.test/feed/"))
                .CheckAsync();

            Assert.Equal(UpdateCheckOutcome.Failed, report.Outcome);
            Assert.Equal("update.signature_invalid", report.ErrorCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HttpFailureIsReportedAsAFailedCheck()
    {
        var settings = Enabled(new FakeSettingsService());
        var keys = CreateKeys();
        var directory = Directory.CreateTempSubdirectory("ecl-update-http-failure");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "update-trust.json"), keys.TrustJson);
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var report = await CreateService(
                    settings,
                    new CountingHttpProvider(handler),
                    new UpdateCheckEnvironment(directory.FullName, "https://example.test/feed/"))
                .CheckAsync();

            Assert.Equal(UpdateCheckOutcome.Failed, report.Outcome);
            Assert.Equal("update.feed_failed", report.ErrorCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FakeSettingsService Enabled(FakeSettingsService settings, string channel = "main")
    {
        settings.Values["launcher.checkUpdates"] = "true";
        settings.Values["launcher.updateChannel"] = channel;
        return settings;
    }

    private static UpdateCheckService CreateService(
        FakeSettingsService settings,
        IHttpClientProvider http,
        UpdateCheckEnvironment environment) =>
        new(settings, http, environment: environment);

    private static UpdateManifest CreateManifest(string version, UpdateChannel channel) =>
        new(
            1,
            "ECL",
            channel,
            ReleaseVersion.Parse(version),
            "windows",
            UpdateArchitecture.X64,
            ReleaseVersion.Parse("1.0.0"),
            new UpdatePackage($"ECL-{version}.zip", 1024, new string('a', 64)),
            [],
            [],
            "Test release",
            DateTimeOffset.Parse("2026-11-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static StubHandler Respond(byte[] manifest, byte[] signature) =>
        new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("update-manifest.sig", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) };
            }

            if (path.EndsWith("update-manifest.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static (string PrivateKeyPem, string TrustJson, string KeyId) CreateKeys()
    {
        const string keyId = "test-key-1";
        using var rsa = RSA.Create(2048);
        var trust = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keys = new[]
            {
                new
                {
                    keyId,
                    algorithm = UpdateSignatureEnvelope.RsaPssSha256,
                    publicKey = rsa.ExportSubjectPublicKeyInfoPem(),
                },
            },
        });
        return (rsa.ExportPkcs8PrivateKeyPem(), trust, keyId);
    }

    private static string Sign(byte[] manifest, string privateKeyPem, string keyId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            keyId,
            algorithm = UpdateSignatureEnvelope.RsaPssSha256,
            signature = Convert.ToBase64String(
                rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
        });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CountingHttpProvider(HttpMessageHandler? handler = null) : IHttpClientProvider
    {
        private readonly HttpMessageHandler _handler = handler ?? new StubHandler(
            _ => throw new InvalidOperationException("不应发起网络请求。"));

        public int Calls { get; private set; }

        public HttpClient CreateClient()
        {
            Calls++;
            return new HttpClient(_handler);
        }
    }
}
