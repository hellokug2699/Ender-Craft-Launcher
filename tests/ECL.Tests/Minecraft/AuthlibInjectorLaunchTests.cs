// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// Authlib-Injector 只对第三方账户生效：Microsoft 与离线账户的启动命令必须保持不变。
/// </summary>
public sealed class AuthlibInjectorLaunchTests
{
    private const string ServerUrl = "https://auth-a.example.com/api/yggdrasil";

    [Fact]
    public async Task ThirdPartyLaunchAddsInjectorAndMojangUserType()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, out _);
        var account = await SignInThirdPartyAsync(provider);
        Assert.Equal(account.Id, (await provider.GetRequiredService<IAccountService>().GetDefaultAccountAsync())!.Id);

        var instance = await CreateInstanceAsync(provider);
        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success, Describe(report));
        var command = report.CommandLine!;
        Assert.Contains($"-javaagent:c:\\cache\\authlib-injector.jar={ServerUrl}", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--userType mojang", command, StringComparison.Ordinal);
        Assert.Contains(
            report.Steps,
            step => step.StepId == "authlib-injector" && step.Status == LaunchStepStatus.Skipped);
    }

    [Fact]
    public async Task MicrosoftLaunchIsUnaffectedByInjector()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, out _);
        var accounts = provider.GetRequiredService<IAccountService>();

        var added = await accounts.AddMicrosoftAsync(
            new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = AccountType.Microsoft,
                Name = "Steve",
                Uuid = "0123456789abcdef0123456789abcdef",
            },
            new AccountCredentials { MinecraftAccessToken = "mc-access", MicrosoftRefreshToken = "ms-refresh" },
            makeDefault: true);
        Assert.True(added.IsSuccess, added.Error?.ToString());

        var instance = await CreateInstanceAsync(provider);
        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success, Describe(report));
        Assert.DoesNotContain("-javaagent", report.CommandLine!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--userType msa", report.CommandLine!, StringComparison.Ordinal);
        Assert.Contains(
            report.Steps,
            step => step.StepId == "authlib-injector" && step.Status == LaunchStepStatus.Skipped);
    }

    [Fact]
    public async Task OfflineLaunchKeepsLegacyUserType()
    {
        using var environment = new TestEnvironment();
        await using var provider = CreateProvider(environment, out _);
        var accounts = provider.GetRequiredService<IAccountService>();
        Assert.True((await accounts.AddOfflineAsync("Steve", makeDefault: true)).IsSuccess);

        var instance = await CreateInstanceAsync(provider);
        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success, Describe(report));
        Assert.DoesNotContain("-javaagent", report.CommandLine!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--userType legacy", report.CommandLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InjectorAcceptsMatchingChecksumAndRejectsMismatch()
    {
        var jar = Encoding.UTF8.GetBytes("fake injector jar");
        var goodHash = Convert.ToHexString(SHA256.HashData(jar)).ToLowerInvariant();

        using (var environment = new TestEnvironment())
        {
            var service = new AuthlibInjectorService(
                new FakeDownloadService(jar, goodHash),
                environment.Paths,
                NullLogger<AuthlibInjectorService>.Instance);
            var result = await service.EnsureAsync();
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.True(File.Exists(result.Value!.Path));
            Assert.Equal("1.2.8", result.Value.Version);
        }

        using (var environment = new TestEnvironment())
        {
            var service = new AuthlibInjectorService(
                new FakeDownloadService(jar, new string('0', 64)),
                environment.Paths,
                NullLogger<AuthlibInjectorService>.Instance);
            var result = await service.EnsureAsync();
            Assert.False(result.IsSuccess);
            Assert.Equal("authlibinjector.checksum_failed", result.Error?.Code);
        }
    }

    private static ServiceProvider CreateProvider(TestEnvironment environment, out StubAuthlib stubAuthlib)
    {
        var authlib = new StubAuthlib();
        stubAuthlib = authlib;
        return environment.CreateServices(services =>
        {
            services.AddSingleton<ISecureCredentialStore, Auth.InMemoryCredentialStore>();
            services.AddSingleton<IAuthlibAuthenticationService>(authlib);
            services.AddSingleton<IAuthlibInjectorService, StubInjector>();
        });
    }

    private static async Task<Account> SignInThirdPartyAsync(ServiceProvider provider)
    {
        var servers = provider.GetRequiredService<IAuthServerService>();
        var server = (await servers.AddAsync(ServerUrl)).Value!;
        var accounts = provider.GetRequiredService<IAccountService>();
        var signedIn = await accounts.SignInThirdPartyAsync(server.Id, "Steve", "pw", makeDefault: true);
        Assert.True(signedIn.IsSuccess, signedIn.Error?.ToString());
        return signedIn.Value!;
    }

    private static async Task<GameInstance> CreateInstanceAsync(ServiceProvider provider)
    {
        var instances = provider.GetRequiredService<IInstanceService>();
        var created = await instances.CreateAsync("AuthlibTest", "1.21.5");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        return created.Value!;
    }

    private static string Describe(LaunchReport report) =>
        string.Join(" | ", report.Steps.Select(step => $"{step.StepId}:{step.Status}:{step.Message}"));

    private sealed class StubInjector : IAuthlibInjectorService
    {
        public Task<Result<AuthlibInjectorArtifact>> EnsureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthlibInjectorArtifact>.Ok(
                new AuthlibInjectorArtifact(@"c:\cache\authlib-injector.jar", "1.2.8")));

        public string? TryGetCachedJarPath() => @"c:\cache\authlib-injector.jar";
    }

    private sealed class StubAuthlib : IAuthlibAuthenticationService
    {
        public Task<Result<AuthServerMetadata>> FetchMetadataAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthServerMetadata>.Ok(new AuthServerMetadata
            {
                Url = url,
                ServerName = "Example Auth",
            }));

        public Task<Result<AuthlibSession>> AuthenticateAsync(
            AuthServer server,
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthlibSession>.Ok(new AuthlibSession
            {
                AccessToken = "ygg-access",
                ClientToken = "ygg-client",
                PlayerName = username,
                Uuid = "01234567-89ab-cdef-0123-456789abcdef",
                AvailableProfiles = 1,
            }));

        public Task<Result<bool>> ValidateAsync(
            AuthServer server,
            string accessToken,
            string clientToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<bool>.Ok(true));

        public Task<Result<AuthlibSession>> RefreshAsync(
            AuthServer server,
            string accessToken,
            string clientToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AuthlibSession>.Fail("unexpected", "tests must not refresh"));
    }

    /// <summary>用真实的 AuthlibInjectorService，只把下载层替换成内存假实现。</summary>
    private sealed class FakeDownloadService(byte[] jar, string sha256) : IDownloadService
    {
        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(request.DestinationPath, jar);
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, jar.Length));
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Serialize(new
            {
                version = "1.2.8",
                download_url = "https://example.com/authlib-injector-1.2.8.jar",
                checksums = new { sha256 },
            }));
    }
}
