// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Ecl.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「网络」页（B3 步骤 5 / D-04）：
/// 结构化端点联合校验与回滚、受保护凭据、测试连接、旧地址迁移，以及 D-02 的如实呈现。
/// </summary>
public sealed class NetworkSettingsPageTests
{
    [Fact]
    public void ManualModeShowsStructuredFieldsAndOnlyVerifiedProtocols()
    {
        using var host = new NetworkHost();

        Assert.False(host.Page.IsManualMode);
        Assert.False(host.Page.ShowManualCard);
        Assert.Equal(
            ["http", "socks5", "socks4a"],
            host.Page.ProtocolChoices.Select(choice => choice.Value));
        Assert.Equal(
            ["HTTP", "SOCKS5", "SOCKS4a"],
            host.Page.ProtocolChoices.Select(choice => choice.DisplayName));

        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;

        Assert.True(host.Page.IsManualMode);
        Assert.True(host.Page.ShowManualCard);
        Assert.True(host.Page.ShowCredentialCard);

        host.Page.Protocol = ProxyProtocols.Socks4a;
        Assert.False(host.Page.ShowCredentialCard);
    }

    [Fact]
    public void ApplyingManualProxyValidatesTheWholeEndpointBeforeWriting()
    {
        using var host = new NetworkHost();
        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;

        host.Page.Host = string.Empty;
        host.Page.PortText = "7890";
        host.Page.ApplyManualCommand.Execute(null);

        Assert.True(host.Page.HasError);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.ProtocolKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.HostKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.PortKey));

        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "not-a-port";
        host.Page.ApplyManualCommand.Execute(null);

        Assert.True(host.Page.HasError);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.ProtocolKey));

        host.Page.Protocol = ProxyProtocols.Socks5;
        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "1080";
        host.Page.ApplyManualCommand.Execute(null);

        Assert.False(host.Page.HasError);
        Assert.False(host.Page.HasChanges);
        Assert.Equal("socks5", host.Settings.GetRawString(ProxySettings.ProtocolKey));
        Assert.Equal("127.0.0.1", host.Settings.GetRawString(ProxySettings.HostKey));
        Assert.Equal("1080", host.Settings.GetRawString(ProxySettings.PortKey));
        Assert.Equal("socks5://127.0.0.1:1080", host.Page.SavedEndpointText);
        Assert.False(string.IsNullOrWhiteSpace(host.Page.StatusMessage));
    }

    [Fact]
    public void AWriteFailureRollsBackTheWholeEndpointAndKeepsTheLastValidConfiguration()
    {
        using var host = new NetworkHost(failingKey: ProxySettings.PortKey);
        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;

        host.Page.Protocol = "http";
        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "7890";
        host.Page.ApplyManualCommand.Execute(null);

        Assert.True(host.Page.HasError);
        Assert.Contains("无法保存", host.Page.ErrorText, StringComparison.Ordinal);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.ProtocolKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.HostKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.PortKey));
    }

    [Fact]
    public void CredentialsUseTheProtectedStoreAndNeverAppearInSettingsText()
    {
        using var host = new NetworkHost();
        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;
        host.Page.Protocol = "http";
        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "7890";
        host.Page.ApplyManualCommand.Execute(null);

        host.Page.CredentialUserName = "alice";
        Assert.True(host.Page.SaveCredential("secret"));
        Assert.True(host.Page.HasCredential);
        Assert.Single(host.Credentials.Saved);
        Assert.Equal("alice", host.Page.CredentialUserName);
        Assert.DoesNotContain(
            "secret",
            File.ReadAllText(host.Paths.GlobalSettingsFile),
            StringComparison.Ordinal);

        // 密码留空时保留原密码，只更新用户名。
        host.Page.CredentialUserName = "alice2";
        Assert.True(host.Page.SaveCredential(string.Empty));
        var stored = Assert.Single(host.Credentials.Saved).Value;
        Assert.Equal("alice2", stored.UserName);
        Assert.Equal("secret", stored.Password);

        host.Dialogs.ConfirmResult = false;
        host.Page.ClearCredentialCommand.Execute(null);
        Assert.True(host.Page.HasCredential);

        host.Dialogs.ConfirmResult = true;
        host.Page.ClearCredentialCommand.Execute(null);
        Assert.False(host.Page.HasCredential);
        Assert.Empty(host.Credentials.Saved);
        Assert.Contains("已清除", host.Page.CredentialStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionUsesTheAppliedEndpointAndItsCredential()
    {
        using var host = new NetworkHost();
        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;
        host.Page.Protocol = "http";
        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "7890";
        host.Page.ApplyManualCommand.Execute(null);
        host.Page.CredentialUserName = "alice";
        Assert.True(host.Page.SaveCredential("secret"));

        host.Page.TestTarget = "https://example.test/";
        await host.Page.TestConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(host.Tester.LastRequest);
        Assert.Equal(ProxySettings.ManualMode, host.Tester.LastRequest!.Mode);
        Assert.Equal("http://127.0.0.1:7890", host.Tester.LastRequest.Endpoint!.ToString());
        Assert.Equal("alice", host.Tester.LastRequest.Credential!.UserName);
        Assert.True(host.Page.TestSucceeded);
        Assert.Contains("HTTP 200", host.Page.TestStatus, StringComparison.Ordinal);

        // 尚未应用的端点不携带已保存凭据，避免把旧凭据发给新目标。
        host.Page.Host = "127.0.0.2";
        await host.Page.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("http://127.0.0.2:7890", host.Tester.LastRequest!.Endpoint!.ToString());
        Assert.Null(host.Tester.LastRequest.Credential);
        Assert.Contains("未应用", host.Page.TestStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyAddressCanBeMigratedAndInvalidLegacyValueIsKeptWithARepairHint()
    {
        using var host = new NetworkHost(seed: settings =>
        {
            settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
            settings.TrySetRaw(ProxySettings.LegacyAddressKey, "http://alice:secret@127.0.0.1:7890", null, out _);
        });

        Assert.True(host.Page.HasLegacyAddress);
        Assert.True(host.Page.CanMigrateLegacy);
        await host.Page.MigrateLegacyCommand.ExecuteAsync(null);

        Assert.Equal("http", host.Settings.GetRawString(ProxySettings.ProtocolKey));
        Assert.Equal("7890", host.Settings.GetRawString(ProxySettings.PortKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.LegacyAddressKey));
        Assert.False(host.Page.HasLegacyAddress);
        Assert.Contains("已迁移", host.Page.StatusMessage, StringComparison.Ordinal);

        using var invalid = new NetworkHost(seed: settings =>
        {
            settings.TrySetRaw(ProxySettings.ModeKey, ProxySettings.ManualMode, null, out _);
            settings.TrySetRaw(ProxySettings.LegacyAddressKey, "127.0.0.1:7890", null, out _);
        });
        Assert.True(invalid.Page.HasLegacyAddress);
        Assert.False(invalid.Page.CanMigrateLegacy);
        Assert.Contains("无法无歧义", invalid.Page.LegacyMessage, StringComparison.Ordinal);
        Assert.Equal("127.0.0.1:7890", invalid.Settings.GetRawString(ProxySettings.LegacyAddressKey));
    }

    [Fact]
    public void ResetClearsOnlyTheNetworkSettingsAndKeepsSavedCredentials()
    {
        using var host = new NetworkHost();
        host.Settings.TrySetRaw("launcher.checkUpdates", "false", null, out _);
        host.Page.ModeEditor.TextValue = ProxySettings.ManualMode;
        host.Page.Protocol = "http";
        host.Page.Host = "127.0.0.1";
        host.Page.PortText = "7890";
        host.Page.ApplyManualCommand.Execute(null);
        host.Page.CredentialUserName = "alice";
        Assert.True(host.Page.SaveCredential("secret"));

        host.Page.ResetToDefaults();

        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.ModeKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.ProtocolKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.HostKey));
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(ProxySettings.PortKey));
        Assert.Equal("false", host.Settings.GetRawString("launcher.checkUpdates"));
        Assert.Single(host.Credentials.Saved);
        Assert.Contains("凭据", host.Page.StatusMessage, StringComparison.Ordinal);
    }

    private sealed class NetworkHost : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public NetworkHost(string? failingKey = null, Action<ISettingsService>? seed = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-network-tests", Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(_root);
            Paths.EnsureCreated();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(Paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            var realSettings = _provider.GetRequiredService<ISettingsService>();
            seed?.Invoke(realSettings);
            Settings = failingKey is null
                ? realSettings
                : new FailingSettingsService(realSettings, failingKey);
            Dialogs = new FakeDialogService();
            Credentials = new FakeCredentialStore();
            Tester = new FakeProxyConnectionTester();
            var localizer = _provider.GetRequiredService<ILocalizer>();
            Page = new SettingsNetworkPageViewModel(
                Settings,
                localizer,
                Dialogs,
                Credentials,
                Tester);
        }

        public AppPaths Paths { get; }

        public ISettingsService Settings { get; }

        public FakeDialogService Dialogs { get; }

        public FakeCredentialStore Credentials { get; }

        public FakeProxyConnectionTester Tester { get; }

        public SettingsNetworkPageViewModel Page { get; }

        public void Dispose()
        {
            _provider.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不应影响测试结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private sealed class FakeCredentialStore : IProxyCredentialStore
    {
        public bool IsSupported { get; set; } = true;

        public Dictionary<string, ProxyCredential> Saved { get; } = new(StringComparer.Ordinal);

        public Result<ProxyCredential?> Read(ProxyEndpoint endpoint) =>
            Saved.TryGetValue(endpoint.ToString(), out var credential)
                ? Result<ProxyCredential?>.Ok(credential)
                : Result<ProxyCredential?>.Ok(null);

        public Result Save(ProxyEndpoint endpoint, ProxyCredential credential)
        {
            Saved[endpoint.ToString()] = credential;
            return Result.Ok();
        }

        public Result Delete(ProxyEndpoint endpoint)
        {
            Saved.Remove(endpoint.ToString());
            return Result.Ok();
        }
    }

    private sealed class FakeProxyConnectionTester : IProxyConnectionTester
    {
        public ProxyTestRequest? LastRequest { get; private set; }

        public Task<ProxyConnectionTestResult> TestAsync(
            ProxyTestRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ProxyConnectionTestResult(
                Reached: true,
                StatusCode: 200,
                Elapsed: TimeSpan.FromMilliseconds(12)));
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string title, string message, string? acknowledgementToken = null) =>
            ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
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
