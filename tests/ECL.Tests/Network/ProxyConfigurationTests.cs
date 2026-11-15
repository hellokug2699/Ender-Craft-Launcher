// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Network;

/// <summary>
/// D-04 结构化代理端点 / 旧 URI 解析 / 迁移：只测试纯配置语义与迁移，不依赖真实网络。
/// </summary>
public sealed class ProxyConfigurationTests
{
    [Theory]
    [InlineData("http", "127.0.0.1", 7890, "http://127.0.0.1:7890")]
    [InlineData("socks5", "Proxy.Local", 1080, "socks5://proxy.local:1080")]
    [InlineData("socks4a", "2001:db8::1", 1080, "socks4a://[2001:db8::1]:1080")]
    public void SupportedProtocolsCreateNormalizedEndpoints(
        string protocol,
        string host,
        int port,
        string expected)
    {
        Assert.True(ProxyEndpoint.TryCreate(protocol, host, port, out var endpoint, out var error));
        Assert.Equal(ProxyEndpointError.None, error);
        Assert.NotNull(endpoint);
        Assert.Equal(expected, endpoint!.ToString());
        Assert.Equal(expected, endpoint.ToUri().ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("https", "127.0.0.1", 7890, ProxyEndpointError.UnsupportedProtocol)]
    [InlineData("ftp", "127.0.0.1", 7890, ProxyEndpointError.UnsupportedProtocol)]
    [InlineData("http", "", 7890, ProxyEndpointError.HostRequired)]
    [InlineData("http", "   ", 7890, ProxyEndpointError.HostRequired)]
    [InlineData("http", "http://127.0.0.1", 7890, ProxyEndpointError.HostInvalid)]
    [InlineData("http", "proxy/path", 7890, ProxyEndpointError.HostInvalid)]
    [InlineData("http", "bad host", 7890, ProxyEndpointError.HostInvalid)]
    [InlineData("http", "127.0.0.1", 0, ProxyEndpointError.PortRequired)]
    [InlineData("http", "127.0.0.1", 65536, ProxyEndpointError.PortOutOfRange)]
    public void InvalidEndpointsAreRejected(
        string protocol,
        string host,
        int port,
        ProxyEndpointError expected)
    {
        Assert.False(ProxyEndpoint.TryCreate(protocol, host, port, out var endpoint, out var error));
        Assert.Null(endpoint);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void PortTextMustBeAnInteger()
    {
        Assert.False(ProxyEndpoint.TryCreate("http", "127.0.0.1", "", out _, out var empty));
        Assert.Equal(ProxyEndpointError.PortRequired, empty);

        Assert.False(ProxyEndpoint.TryCreate("http", "127.0.0.1", "abc", out _, out var invalid));
        Assert.Equal(ProxyEndpointError.PortInvalid, invalid);

        Assert.True(ProxyEndpoint.TryCreate("http", "[::1]", "8080", out var endpoint, out _));
        Assert.Equal("http://[::1]:8080", endpoint!.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:7890", "http", "127.0.0.1", 7890)]
    [InlineData("SOCKS5://proxy.local:1080", "socks5", "proxy.local", 1080)]
    [InlineData("socks4a://[::1]:1080", "socks4a", "::1", 1080)]
    [InlineData("http://host", "http", "host", 80)]
    public void LegacyAddressesParseIntoEndpoints(
        string raw,
        string protocol,
        string host,
        int port)
    {
        Assert.True(ProxySettings.TryParseLegacy(raw, out var endpoint, out var user, out var password, out var error));
        Assert.Equal(ProxyEndpointError.None, error);
        Assert.NotNull(endpoint);
        Assert.Equal(protocol, endpoint!.Protocol);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Null(user);
        Assert.Null(password);
    }

    [Fact]
    public void LegacyUserInfoIsSplitButNeverCopiedIntoTheEndpoint()
    {
        Assert.True(ProxySettings.TryParseLegacy(
            "http://alice:p%40ss@127.0.0.1:7890",
            out var endpoint,
            out var user,
            out var password,
            out _));

        Assert.Equal("http://127.0.0.1:7890", endpoint!.ToString());
        Assert.Equal("alice", user);
        Assert.Equal("p@ss", password);
        Assert.DoesNotContain("alice", endpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("p@ss", endpoint.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1:7890")]
    [InlineData("https://127.0.0.1:7890")]
    [InlineData("http://127.0.0.1:7890/path")]
    [InlineData("http://127.0.0.1:7890?q=1")]
    [InlineData("http://:7890")]
    [InlineData("http://127.0.0.1:notaport")]
    public void AmbiguousLegacyAddressesAreRejected(string raw)
    {
        Assert.False(ProxySettings.TryParseLegacy(raw, out _, out _, out _, out var error));
        Assert.NotEqual(ProxyEndpointError.None, error);
    }

    [Fact]
    public void ResolverPrefersStructuredEndpointAndNeverFallsBackToLegacy()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "http://127.0.0.1:7890", null, out _);
        settings.TrySetRaw(ProxySettings.ProtocolKey, "socks5", null, out _);
        settings.TrySetRaw(ProxySettings.HostKey, "proxy.local", null, out _);
        settings.TrySetRaw(ProxySettings.PortKey, "1080", null, out _);

        var resolved = ProxySettings.Resolve(settings);
        Assert.Equal(ProxyMode.Manual, resolved.Mode);
        Assert.Equal("socks5://proxy.local:1080", resolved.Endpoint!.ToString());
        Assert.True(resolved.IsValid);

        // 结构化端点无效时即使旧地址可解析，也必须显式失败，不能退回旧地址/系统代理。
        // 元数据校验会拒绝非法主机，这里用文件级种子模拟损坏/手改的配置。
        File.WriteAllText(
            environment.Paths.GlobalSettingsFile,
            "{\"schemaVersion\":1,\"values\":{"
            + "\"network.proxyMode\":\"manual\","
            + "\"network.proxyAddress\":\"http://127.0.0.1:7890\","
            + "\"network.proxyProtocol\":\"socks5\","
            + "\"network.proxyHost\":\"bad host\","
            + "\"network.proxyPort\":1080}}");
        using var corrupted = environment.CreateServices();
        var invalid = ProxySettings.Resolve(corrupted.GetRequiredService<ISettingsService>());
        Assert.False(invalid.IsValid);
        Assert.Null(invalid.Endpoint);
        Assert.Equal("network.proxy.host_invalid", invalid.ErrorCode);
    }

    [Fact]
    public void ResolverUsesLegacyAddressOnlyWhenStructuredKeysAreAbsent()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "socks5://127.0.0.1:1080", null, out _);

        var resolved = ProxySettings.Resolve(settings);
        Assert.Equal("socks5://127.0.0.1:1080", resolved.Endpoint!.ToString());

        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "not a uri", null, out _);
        var invalid = ProxySettings.Resolve(settings);
        Assert.False(invalid.IsValid);
        Assert.Equal("network.proxy.legacy_invalid", invalid.ErrorCode);
    }

    [Fact]
    public void ResolverReportsUnknownModeInsteadOfTreatingItAsSystem()
    {
        using var environment = new TestEnvironment();
        // 元数据校验会拒绝非法模式；这里用文件级种子模拟损坏/手改的配置。
        File.WriteAllText(
            environment.Paths.GlobalSettingsFile,
            "{\"schemaVersion\":1,\"values\":{\"network.proxyMode\":\"bogus\"}}");

        using var second = environment.CreateServices();
        var reloaded = second.GetRequiredService<ISettingsService>();
        var resolved = ProxySettings.Resolve(reloaded);
        Assert.False(resolved.IsValid);
        Assert.Equal("network.proxy.invalid_mode", resolved.ErrorCode);
        Assert.Equal(ProxyMode.Manual, resolved.Mode);
    }

    [Fact]
    public void MigrationSplitsLegacyAddressAndClearsTheOldValue()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "http://alice:secret@127.0.0.1:7890", null, out _);

        var store = new FakeCredentialStore();
        var result = ProxySettingsMigration.TryMigrate(settings, store);

        Assert.Equal(ProxyMigrationStatus.Migrated, result.Status);
        Assert.Equal("http", settings.GetRawString(ProxySettings.ProtocolKey));
        Assert.Equal("127.0.0.1", settings.GetRawString(ProxySettings.HostKey));
        Assert.Equal("7890", settings.GetRawString(ProxySettings.PortKey));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.LegacyAddressKey));
        var credential = Assert.Single(store.Saved);
        Assert.Equal("http://127.0.0.1:7890", credential.Key);
        Assert.Equal("alice", credential.Value.UserName);
        Assert.Equal("secret", credential.Value.Password);

        // 幂等：再次运行不重写，也不重新保存凭据。
        var again = ProxySettingsMigration.TryMigrate(settings, store);
        Assert.Equal(ProxyMigrationStatus.NothingToDo, again.Status);
        Assert.Single(store.Saved);
    }

    [Fact]
    public void MigrationKeepsAnUnambiguousFailureAsTheOriginalValue()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "127.0.0.1:7890", null, out _);

        var result = ProxySettingsMigration.TryMigrate(settings, new FakeCredentialStore());

        Assert.Equal(ProxyMigrationStatus.Unresolved, result.Status);
        Assert.True(result.LegacyRetained);
        Assert.Equal("127.0.0.1:7890", settings.GetRawString(ProxySettings.LegacyAddressKey));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.ProtocolKey));
    }

    [Fact]
    public void MigrationClearsTheOldValueWhenStructuredConfigurationAlreadyExists()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.ProtocolKey, "http", null, out _);
        settings.TrySetRaw(ProxySettings.HostKey, "127.0.0.1", null, out _);
        settings.TrySetRaw(ProxySettings.PortKey, "7890", null, out _);
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "http://127.0.0.1:7890", null, out _);

        var result = ProxySettingsMigration.TryMigrate(settings, new FakeCredentialStore());

        Assert.Equal(ProxyMigrationStatus.NothingToDo, result.Status);
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.LegacyAddressKey));
        Assert.Equal("7890", settings.GetRawString(ProxySettings.PortKey));
    }

    [Fact]
    public void MigrationRollsBackPartialStructuredWritesAndKeepsTheLegacyValue()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var real = provider.GetRequiredService<ISettingsService>();
        real.TrySetRaw(ProxySettings.LegacyAddressKey, "http://127.0.0.1:7890", null, out _);
        var settings = new FailingSettingsService(real, ProxySettings.PortKey);

        var result = ProxySettingsMigration.TryMigrate(settings, new FakeCredentialStore());

        Assert.Equal(ProxyMigrationStatus.Failed, result.Status);
        Assert.True(result.LegacyRetained);
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.ProtocolKey));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.HostKey));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.PortKey));
        Assert.Equal("http://127.0.0.1:7890", settings.GetRawString(ProxySettings.LegacyAddressKey));
    }

    [Fact]
    public void MigrationDoesNotClaimSuccessWhenProtectedCredentialsCannotBeSaved()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.TrySetRaw(ProxySettings.LegacyAddressKey, "http://alice:secret@127.0.0.1:7890", null, out _);

        var store = new FakeCredentialStore { FailSave = true };
        var result = ProxySettingsMigration.TryMigrate(settings, store);

        Assert.Equal(ProxyMigrationStatus.Unresolved, result.Status);
        Assert.True(result.LegacyRetained);
        Assert.Equal("http://alice:secret@127.0.0.1:7890", settings.GetRawString(ProxySettings.LegacyAddressKey));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource(ProxySettings.ProtocolKey));
    }

    [Fact]
    public void WindowsDpapiStoreRoundTripsAndNeverStoresPlaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = new TestEnvironment();
        var store = new WindowsDpapiProxyCredentialStore(
            environment.Paths,
            NullLogger<WindowsDpapiProxyCredentialStore>.Instance);
        var endpoint = ProxyEndpoint.TryCreate("http", "127.0.0.1", 7890, out var created, out _)
            ? created!
            : throw new InvalidOperationException("测试端点无效。");

        Assert.True(store.IsSupported);
        Assert.True(store.Save(endpoint, new ProxyCredential("alice", "secret")).IsSuccess);

        var file = Path.Combine(environment.Paths.ConfigDirectory, "proxy-credentials.dat");
        Assert.True(File.Exists(file));
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(-1, IndexOf(bytes, System.Text.Encoding.UTF8.GetBytes("alice")));
        Assert.Equal(-1, IndexOf(bytes, System.Text.Encoding.UTF8.GetBytes("secret")));

        var read = store.Read(endpoint);
        Assert.True(read.IsSuccess);
        Assert.Equal("alice", read.Value!.UserName);
        Assert.Equal("secret", read.Value.Password);

        Assert.True(store.Delete(endpoint).IsSuccess);
        Assert.Null(store.Read(endpoint).Value);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class FakeCredentialStore : IProxyCredentialStore
    {
        public bool FailSave { get; set; }

        public bool IsSupported => true;

        public Dictionary<string, ProxyCredential> Saved { get; } = new(StringComparer.Ordinal);

        public Result<ProxyCredential?> Read(ProxyEndpoint endpoint) =>
            Saved.TryGetValue(endpoint.ToString(), out var credential)
                ? Result<ProxyCredential?>.Ok(credential)
                : Result<ProxyCredential?>.Ok(null);

        public Result Save(ProxyEndpoint endpoint, ProxyCredential credential)
        {
            if (FailSave)
            {
                return Result.Fail("test.save_failed", "保存失败（测试）。");
            }

            Saved[endpoint.ToString()] = credential;
            return Result.Ok();
        }

        public Result Delete(ProxyEndpoint endpoint)
        {
            Saved.Remove(endpoint.ToString());
            return Result.Ok();
        }
    }

    private sealed class FailingSettingsService(ISettingsService inner, string failingKey) : ISettingsService
    {
        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) =>
            inner.GetDefinitions(scope);

        public OptionDefinition GetDefinition(string key) => inner.GetDefinition(key);

        public T? Get<T>(string key, string? instanceId = null) => inner.Get<T>(key, instanceId);

        public string GetRawString(string key, string? instanceId = null) =>
            inner.GetRawString(key, instanceId);

        public SettingsValueSource GetSource(string key, string? instanceId = null) =>
            inner.GetSource(key, instanceId);

        public bool IsVisible(OptionDefinition definition, string? instanceId = null) =>
            inner.IsVisible(definition, instanceId);

        public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
        {
            if (string.Equals(key, failingKey, StringComparison.Ordinal))
            {
                throw new IOException("模拟写盘失败。");
            }

            return inner.TrySetRaw(key, rawValue, instanceId, out error);
        }

        public bool TryClear(string key, string? instanceId, out string? error) =>
            inner.TryClear(key, instanceId, out error);
    }
}
