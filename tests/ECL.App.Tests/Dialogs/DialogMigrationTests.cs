// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Tests.Support;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Xunit;

namespace Ecl.App.Tests.Dialogs;

/// <summary>
/// B10 版本导入与 Authlib 账户弹窗（ADR 0052）：内容走 ViewModel + DataTemplate，
/// 结果走统一 Dialog Engine 契约；批量导入登记统一 Operation，密码不进入绑定属性。
/// </summary>
public sealed class DialogMigrationTests
{
    [Fact]
    public async Task VersionImportOnlyProcessesImportableSelectedVersions()
    {
        var host = new VersionImportHost();
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);

        Assert.Equal(3, host.ViewModel.Items.Count);
        Assert.True(host.ViewModel.Items[0].IsChecked);
        Assert.False(host.ViewModel.Items[1].CanImport);
        Assert.False(host.ViewModel.Items[1].IsChecked);
        Assert.False(host.ViewModel.Items[2].CanImport);

        await host.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.Equal("1.20.1", Assert.Single(host.Instances.ImportedIds));
        Assert.Equal(DialogOutcome.Primary, session.Result!.Outcome);
        var result = Assert.IsType<VersionImportResult>(session.Result.Value);
        Assert.Equal(1, result.ImportedCount);
        Assert.False(host.ViewModel.Items[0].IsChecked);
        Assert.False(host.ViewModel.Items[0].CanImport);
    }

    [Fact]
    public async Task VersionImportRegistersAPluggableOperationInTheOutputPipeline()
    {
        var host = new VersionImportHost();
        Attach(host.ViewModel, new FakeDialogSession());

        await host.ViewModel.ImportCommand.ExecuteAsync(null);

        var start = Assert.Single(host.Operations.Starts);
        Assert.Equal(Ecl.Operations.OperationKind.Import, start.Kind);
        Assert.Equal(1, start.Total);
        Assert.True(start.CanCancel);
        Assert.NotNull(host.Operations.Handle);
        Assert.True(host.Operations.Handle!.Completed);
        Assert.False(host.Operations.Handle.Failed);
    }

    [Fact]
    public async Task VersionImportKeepsTheDialogOpenAndReportsEveryFailure()
    {
        var host = new VersionImportHost(importSucceeds: false);
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);

        await host.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.True(session.IsOpen);
        Assert.Null(session.Result);
        Assert.Contains("import failed", host.ViewModel.StatusText);
        Assert.Contains("1.20.1", host.ViewModel.StatusText);
        Assert.True(host.Operations.Handle!.Failed);
        Assert.False(host.Operations.Handle.Completed);
        // 失败的版本保持可再次选择，不伪造已导入。
        Assert.True(host.ViewModel.Items[0].IsChecked);
        Assert.True(host.ViewModel.Items[0].CanImport);
    }

    [Fact]
    public async Task VersionImportWithoutSelectionDoesNotStartAnOperation()
    {
        var host = new VersionImportHost();
        host.ViewModel.Items[0].IsChecked = false;
        Attach(host.ViewModel, new FakeDialogSession());

        await host.ViewModel.ImportCommand.ExecuteAsync(null);

        Assert.Empty(host.Operations.Starts);
        Assert.Contains("Select at least one", host.ViewModel.StatusText);
    }

    [Fact]
    public async Task AuthlibCreateValidatesRequiredFieldsBeforeCallingTheDomain()
    {
        var host = new AuthlibHost();
        Attach(host.ViewModel, new FakeDialogSession());

        await host.ViewModel.SubmitAsync(string.Empty);
        Assert.Contains("auth server address", host.ViewModel.StatusText);

        host.ViewModel.ServerUrl = "https://example.com/api/yggdrasil";
        await host.ViewModel.SubmitAsync("password");
        Assert.Contains("username", host.ViewModel.StatusText);

        host.ViewModel.Username = "Player";
        await host.ViewModel.SubmitAsync(string.Empty);
        Assert.Contains("password", host.ViewModel.StatusText);

        Assert.Empty(host.Servers.Calls);
        Assert.Empty(host.Accounts.Calls);
    }

    [Fact]
    public async Task AuthlibCreateSignsInAddsTheServerAndClosesWithTheAccount()
    {
        var host = new AuthlibHost();
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);
        host.ViewModel.ServerUrl = "https://example.com/api/yggdrasil";
        host.ViewModel.Username = "Player";

        await host.ViewModel.SubmitAsync("secret");

        Assert.Equal(new[] { "add" }, host.Servers.Calls);
        Assert.Contains(host.Accounts.Calls, call => call.StartsWith("sign-in:server-1:Player", StringComparison.Ordinal));
        Assert.Equal(DialogOutcome.Primary, session.Result!.Outcome);
        var result = Assert.IsType<AuthlibAccountResult>(session.Result.Value);
        Assert.Equal("signed-in", result.Account.Name);
        Assert.False(host.ViewModel.IsBusy);
    }

    [Fact]
    public async Task AuthlibSettingsSaveWithoutPasswordKeepsStoredCredentials()
    {
        var host = new AuthlibHost(settingsMode: true);
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);

        await host.ViewModel.SubmitAsync(string.Empty);

        Assert.Equal(new[] { "update" }, host.Servers.Calls);
        Assert.DoesNotContain(host.Accounts.Calls, call => call.StartsWith("reauth:", StringComparison.Ordinal));
        Assert.Equal(DialogOutcome.Primary, session.Result!.Outcome);
        var result = Assert.IsType<AuthlibAccountResult>(session.Result.Value);
        Assert.Same(host.Account, result.Account);
        Assert.True(host.ViewModel.IsUsernameReadOnly);
    }

    [Fact]
    public async Task AuthlibServerAddressChangeRequiresAPasswordBeforeAnyWrite()
    {
        var host = new AuthlibHost(settingsMode: true);
        Attach(host.ViewModel, new FakeDialogSession());
        host.ViewModel.ServerUrl = "https://other.example/api/yggdrasil";

        await host.ViewModel.SubmitAsync(string.Empty);

        Assert.Contains("sign in again", host.ViewModel.StatusText);
        Assert.Empty(host.Servers.Calls);
        Assert.Empty(host.Accounts.Calls);
    }

    [Fact]
    public async Task AuthlibServerAddressChangeValidatesLogsInThenCommitsAndRollsBackOnFailure()
    {
        var host = new AuthlibHost(settingsMode: true);
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);
        host.ViewModel.ServerUrl = "https://other.example/api/yggdrasil";

        await host.ViewModel.SubmitAsync("new-password");

        Assert.Equal(new[] { "validate", "update" }, host.Servers.Calls);
        Assert.Contains(host.Accounts.Calls, call => call.StartsWith("prepare:", StringComparison.Ordinal));
        Assert.Contains(host.Accounts.Calls, call => call.StartsWith("apply:", StringComparison.Ordinal));
        Assert.Equal(DialogOutcome.Primary, session.Result!.Outcome);
    }

    [Fact]
    public async Task AuthlibFailedCredentialCommitRollsBackTheServerAddress()
    {
        var host = new AuthlibHost(settingsMode: true);
        host.Accounts.ApplyResult = Result<Account>.Fail("auth.save_failed", "credential store failed");
        var session = new FakeDialogSession();
        Attach(host.ViewModel, session);
        host.ViewModel.ServerUrl = "https://other.example/api/yggdrasil";

        await host.ViewModel.SubmitAsync("new-password");

        // 提交失败时必须先尝试把地址改回旧值，避免“新服务器 + 旧凭据”。
        Assert.Equal(new[] { "validate", "update", "update" }, host.Servers.Calls);
        Assert.Null(session.Result);
        Assert.Contains("credential store failed", host.ViewModel.StatusText);
    }

    [Fact]
    public void AuthlibRegisterLinkUsesTheSharedExternalLinkBoundary()
    {
        var host = new AuthlibHost();
        host.ViewModel.RegisterUrl = "not a url";
        host.ViewModel.OpenRegisterCommand.Execute(null);
        Assert.Empty(host.Links.Opened);

        host.ViewModel.RegisterUrl = "https://example.com/register";
        host.ViewModel.OpenRegisterCommand.Execute(null);
        Assert.Equal(new[] { "https://example.com/register" }, host.Links.Opened);

        host.Links.Result = false;
        host.ViewModel.OpenRegisterCommand.Execute(null);
        Assert.Contains("registration page", host.ViewModel.StatusText);
    }

    [Fact]
    public void TheLegacyWindowsAreGoneAndTheNewContentTemplatesExist()
    {
        var app = ReadSource("src/ECL.App/App.xaml");
        Assert.Contains("VersionImportDialogViewModel", app, StringComparison.Ordinal);
        Assert.Contains("AuthlibAccountDialogViewModel", app, StringComparison.Ordinal);

        Assert.False(File.Exists(SourcePath("src/ECL.App/Views/VersionImportDialog.xaml")));
        Assert.False(File.Exists(SourcePath("src/ECL.App/Views/AuthlibAccountDialog.xaml")));
        Assert.True(File.Exists(SourcePath("src/ECL.App/Dialogs/Views/VersionImportDialogView.xaml")));
        Assert.True(File.Exists(SourcePath("src/ECL.App/Dialogs/Views/AuthlibAccountDialogView.xaml")));
    }

    [Fact]
    public void MigratedDialogContentLoadsUnderTheSharedTheme()
    {
        using var ui = new UiThread();
        ui.Invoke(() =>
        {
            TestApplication.Ensure();
            _ = new Ecl.App.Dialogs.Views.VersionImportDialogView();
            _ = new Ecl.App.Dialogs.Views.AuthlibAccountDialogView();
        });
    }

    private static string SourcePath(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ReadSource(string relativePath)
    {
        var path = SourcePath(relativePath);
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return File.ReadAllText(path);
    }

    private sealed class VersionImportHost
    {
        public VersionImportHost(bool importSucceeds = true)
        {
            Operations = new RecordingOperationService();
            Instances = new FakeInstanceService();
            if (!importSucceeds)
            {
                Instances.Import = (id, _) => Task.FromResult(Result<GameInstance>.Fail(
                    "instance.duplicate_name",
                    $"import failed for {id}"));
            }

            ViewModel = new VersionImportDialogViewModel(
                Instances,
                Operations,
                new JsonLocalizer(CultureInfo.GetCultureInfo("en-US")),
                [
                    new DetectedVersion("1.20.1", "1.20.1", "vanilla", true, @"C:\game\1.20.1", false),
                    new DetectedVersion("1.19.2", "1.19.2", "forge", true, @"C:\game\1.19.2", true),
                    new DetectedVersion("broken", "?", "vanilla", true, @"C:\game\broken", false,
                        DetectedVersionProblem.InvalidManifest, "missing mainClass"),
                ]);
        }

        public RecordingOperationService Operations { get; }

        public FakeInstanceService Instances { get; }

        public VersionImportDialogViewModel ViewModel { get; }
    }

    private sealed class AuthlibHost
    {
        public AuthlibHost(bool settingsMode = false)
        {
            Servers = new FakeAuthServerService();
            Accounts = new FakeAccountService();
            Links = new FakeLinkService();
            Account = FakeAccountService.Account("settings-account");
            Server = new AuthServer
            {
                Id = "server-1",
                Url = "https://example.com/api/yggdrasil",
                Name = "Example",
                RegisterUrl = "https://example.com/register",
            };
            ViewModel = new AuthlibAccountDialogViewModel(
                Servers,
                Accounts,
                Links,
                new JsonLocalizer(CultureInfo.GetCultureInfo("en-US")),
                settingsMode ? Server : null,
                settingsMode ? Account : null);
        }

        public FakeAuthServerService Servers { get; }

        public FakeAccountService Accounts { get; }

        public FakeLinkService Links { get; }

        public Account Account { get; }

        public AuthServer Server { get; }

        public AuthlibAccountDialogViewModel ViewModel { get; }
    }

    private static void Attach(IDialogSessionAware viewModel, IDialogSession session) =>
        viewModel.Attach(session);
}
