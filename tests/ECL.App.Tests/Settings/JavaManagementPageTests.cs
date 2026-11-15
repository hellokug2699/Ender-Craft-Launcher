// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「Java 管理」页（java-management.md）：
/// 首次进入才扫描、真实验证状态、全局候选顺序与持久化、移除只解除登记、
/// 卸载仅限 ECL 托管且未被引用、添加/验证的错误反馈与真实失败原因。
/// </summary>
public sealed class JavaManagementPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task ActivationScansOnceAndValidatesEveryRow()
    {
        using var host = new Host();
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(host.Home("jdk21")));
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(
            host.Home("jdk17"),
            version: "17.0.9",
            vendor: "Microsoft"));

        await host.Page.ActivateAsync();

        Assert.Equal(2, host.Page.Runtimes.Count);
        Assert.Equal(1, host.Locator.RefreshCalls);
        Assert.Equal(2, host.Validator.ValidatedPaths.Count);
        Assert.All(host.Page.Runtimes, row => Assert.Equal(JavaRuntimeValidationState.Available, row.State));

        // 同一运行周期内再次进入页面复用结果，不重复扫描。
        await host.Page.ActivateAsync();
        host.Page.Activate();
        Assert.Equal(1, host.Locator.RefreshCalls);
        Assert.Equal(2, host.Validator.ValidatedPaths.Count);
    }

    [Fact]
    public async Task RowsPresentTheMajorVersionNamePathAndRealOrigin()
    {
        using var host = new Host();
        var discoveredHome = host.Home("jdk17");
        var microsoftRuntime = JavaTestRuntimes.Create(
            discoveredHome,
            version: "17.0.9",
            vendor: "Microsoft");
        host.Locator.Discovered.Add(microsoftRuntime);
        host.Validator.Results[microsoftRuntime.ExecutablePath] = new JavaRuntimeValidation(
            JavaRuntimeValidationState.Available,
            new JavaVersion(17, 0, 9, "17.0.9"),
            "Microsoft",
            "amd64",
            JavaRuntimeValidationFailure.None,
            null,
            TimeSpan.FromMilliseconds(8));
        var managedHome = Path.Combine(host.Paths.RuntimesDirectory, "java-runtime-gamma");
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(managedHome, version: "21.0.1", managed: true));

        await host.Page.ActivateAsync();

        var microsoft = host.Page.Runtimes.Single(row => row.HomePath == discoveredHome);
        Assert.Equal("17", microsoft.MajorVersionText);
        Assert.Equal("Microsoft 17.0.9", microsoft.Name);
        Assert.Equal(discoveredHome, microsoft.Path);
        Assert.Equal("已发现", microsoft.OriginText);
        Assert.Equal("可用", microsoft.StateText);
        Assert.False(microsoft.CanUninstall);

        var managed = host.Page.Runtimes.Single(row => row.HomePath == managedHome);
        Assert.Equal("ECL 托管", managed.OriginText);
        Assert.True(managed.CanUninstall);
        Assert.Contains("可卸载", managed.UninstallHint);
    }

    [Fact]
    public async Task ExplicitPriorityComesFirstAndLegacySearchPathOrderStaysCompatible()
    {
        using var host = new Host();
        var a = JavaTestRuntimes.Create(host.Home("jdk-a"), version: "17.0.9");
        var b = JavaTestRuntimes.Create(host.Home("jdk-b"), version: "21.0.1");
        var c = JavaTestRuntimes.Create(host.Home("jdk-c"), version: "25.0.0");
        host.Locator.Discovered.AddRange([a, b, c]);

        host.Settings.TrySetRaw(JavaPriorityOrdering.SettingsKey, b.ExecutablePath, null, out _);
        await host.Page.ActivateAsync();

        Assert.Equal(
            [b.HomePath, a.HomePath, c.HomePath],
            host.Page.Runtimes.Select(row => row.HomePath));

        // 旧配置（java.defaultPath）在尚未保存用户优先级时仍决定候选顺序。
        host.Settings.TryClear(JavaPriorityOrdering.SettingsKey, null, out _);
        host.Settings.TrySetRaw("java.defaultPath", c.HomePath, null, out _);
        await host.Page.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(c.HomePath, host.Page.Runtimes[0].HomePath);
    }

    [Fact]
    public async Task ReorderingPersistsTheWholeListAndIsTheOnlyThingThatCreatesPriority()
    {
        using var host = new Host();
        var a = JavaTestRuntimes.Create(host.Home("jdk-a"));
        var b = JavaTestRuntimes.Create(host.Home("jdk-b"));
        host.Locator.Discovered.AddRange([a, b]);
        await host.Page.ActivateAsync();

        // 未调整顺序前不写入虚构的用户优先级。
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(JavaPriorityOrdering.SettingsKey));

        Assert.True(host.Page.MoveRow(host.Page.Runtimes[1], 0));

        Assert.Equal(
            [b.ExecutablePath, a.ExecutablePath],
            host.Page.Runtimes.Select(row => row.ExecutablePath));
        Assert.Equal(
            $"{b.ExecutablePath};{a.ExecutablePath}",
            host.Settings.GetRawString(JavaPriorityOrdering.SettingsKey));
        Assert.Equal(SettingsValueSource.Global, host.Settings.GetSource(JavaPriorityOrdering.SettingsKey));
        Assert.Contains("已保存", host.Page.StatusMessage);
    }

    [Fact]
    public async Task RemovingOnlyUnregistersTheCandidateAndNeverWritesInstanceSettings()
    {
        using var host = new Host();
        var runtime = JavaTestRuntimes.Create(host.Home("jdk17"));
        host.Locator.Discovered.Add(runtime);
        await host.Page.ActivateAsync();

        // 取消确认不改变任何状态。
        host.Dialogs.ConfirmResult = false;
        host.Page.RemoveCommand.Execute(host.Page.Runtimes[0]);
        Assert.Single(host.Page.Runtimes);
        Assert.False(host.Registry.IsRemoved(runtime.ExecutablePath));

        host.Dialogs.ConfirmResult = true;
        host.Page.RemoveCommand.Execute(host.Page.Runtimes[0]);

        Assert.Empty(host.Page.Runtimes);
        Assert.True(host.Registry.IsRemoved(runtime.ExecutablePath));
        Assert.True(Directory.Exists(host.Paths.Root));
        // 移除不是排序：没有用户优先级时不写入 java.priority。
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(JavaPriorityOrdering.SettingsKey));

        // 页面没有任何实例写入依赖：移除不会改写实例固定引用。
        Assert.DoesNotContain(
            typeof(SettingsJavaPageViewModel).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(Ecl.Minecraft.Instances.IInstanceService));
    }

    [Fact]
    public async Task ExternalRuntimesCannotBeUninstalledAndTheServiceIsNeverCalled()
    {
        using var host = new Host();
        var runtime = JavaTestRuntimes.Create(host.Home("external-jdk"));
        host.Locator.Discovered.Add(runtime);
        await host.Page.ActivateAsync();

        var row = host.Page.Runtimes[0];
        Assert.False(row.CanUninstall);
        Assert.Contains("不是由 ECL 安装", row.UninstallHint);

        host.Page.UninstallCommand.Execute(row);
        await DrainAsync(host.Page);

        Assert.Empty(host.Uninstall.PlannedPaths);
        Assert.Empty(host.Uninstall.UninstalledPaths);
        Assert.Single(host.Page.Runtimes);
        Assert.Contains("无法卸载", host.Page.StatusMessage);
    }

    [Fact]
    public async Task ManagedRuntimeUninstallsOnlyAfterConfirmationAndFailureKeepsTheRow()
    {
        using var host = new Host();
        var home = Path.Combine(host.Paths.RuntimesDirectory, "java-runtime-gamma");
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(home, managed: true));
        await host.Page.ActivateAsync();

        var row = host.Page.Runtimes[0];
        Assert.True(row.CanUninstall);

        host.Dialogs.ConfirmResult = false;
        host.Page.UninstallCommand.Execute(row);
        await DrainAsync(host.Page);
        Assert.Empty(host.Uninstall.UninstalledPaths);
        Assert.Single(host.Page.Runtimes);

        host.Dialogs.ConfirmResult = true;
        host.Page.UninstallCommand.Execute(row);
        await DrainAsync(host.Page);
        Assert.Equal([row.ExecutablePath], host.Uninstall.UninstalledPaths);
        Assert.Empty(host.Page.Runtimes);
        Assert.Contains("已卸载", host.Page.StatusMessage);

        // 卸载失败时保留该行并显示真实原因。
        var second = JavaTestRuntimes.Create(
            Path.Combine(host.Paths.RuntimesDirectory, "java-runtime-delta"),
            managed: true);
        host.Locator.Discovered.Clear();
        host.Locator.Discovered.Add(second);
        await host.Page.RefreshCommand.ExecuteAsync(null);
        host.Uninstall.Result = JavaUninstallResult.Fail("file in use");
        host.Page.UninstallCommand.Execute(host.Page.Runtimes.Single(item => item.HomePath == second.HomePath));
        await DrainAsync(host.Page);

        Assert.Contains("卸载失败", host.Page.StatusMessage);
        Assert.Single(host.Page.Runtimes);
    }

    [Fact]
    public async Task BlockedUninstallExplainsTheRealReason()
    {
        using var host = new Host();
        var home = Path.Combine(host.Paths.RuntimesDirectory, "java-runtime-gamma");
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(home, managed: true));
        host.Uninstall.PlanFactory = runtime => new JavaUninstallPlan(
            runtime,
            IsManaged: true,
            runtime.HomePath,
            [new JavaUninstallBlocker(JavaUninstallBlockerKind.InstancePinned, "My Instance (abc123)")]);

        await host.Page.ActivateAsync();

        var row = host.Page.Runtimes[0];
        Assert.False(row.CanUninstall);
        Assert.Contains("My Instance (abc123)", row.UninstallHint);

        host.Page.UninstallCommand.Execute(row);
        await DrainAsync(host.Page);

        Assert.Empty(host.Uninstall.UninstalledPaths);
        Assert.Contains("无法卸载", host.Page.StatusMessage);
        Assert.Contains("固定引用", host.Page.StatusMessage);
    }

    [Fact]
    public async Task AddingJavaRejectsUnknownAnd32BitRuntimesThenRegistersRealOnes()
    {
        using var host = new Host();
        var unknownPath = Path.Combine(host.Home("unknown"), "bin", "java.exe");
        host.Dialogs.PickOpenFileResult = unknownPath;

        await host.Page.AddJavaCommand.ExecuteAsync(null);
        Assert.Contains("不是可识别的 Java", host.Dialogs.LastError);

        var thirtyTwo = JavaTestRuntimes.Create(host.Home("jdk8-32"), version: "1.8.0_402", architecture: "x86");
        host.Locator.ByPath[thirtyTwo.ExecutablePath] = thirtyTwo;
        host.Validator.Results[thirtyTwo.ExecutablePath] = new JavaRuntimeValidation(
            JavaRuntimeValidationState.Unsupported32Bit,
            new JavaVersion(8, 0, 402, "1.8.0_402"),
            "Oracle",
            "x86",
            JavaRuntimeValidationFailure.None,
            null,
            TimeSpan.FromMilliseconds(5));
        host.Dialogs.PickOpenFileResult = thirtyTwo.ExecutablePath;

        await host.Page.AddJavaCommand.ExecuteAsync(null);
        Assert.Contains("32 位", host.Dialogs.LastError);
        Assert.False(host.Registry.IsImported(thirtyTwo.ExecutablePath));

        var external = JavaTestRuntimes.Create(host.Home("temurin21"), version: "21.0.1", vendor: "Eclipse Adoptium");
        host.Locator.ByPath[external.ExecutablePath] = external;
        host.Dialogs.PickOpenFileResult = external.ExecutablePath;

        await host.Page.AddJavaCommand.ExecuteAsync(null);

        Assert.True(host.Registry.IsImported(external.ExecutablePath));
        var row = Assert.Single(host.Page.Runtimes);
        Assert.Equal(external.HomePath, row.HomePath);
        Assert.Equal("已登记", row.OriginText);
        Assert.Equal(JavaRuntimeValidationState.Available, row.State);
    }

    [Fact]
    public async Task AddingAnExistingJavaReportsItInsteadOfDuplicating()
    {
        using var host = new Host();
        var runtime = JavaTestRuntimes.Create(host.Home("jdk17"));
        host.Locator.Discovered.Add(runtime);
        host.Locator.ByPath[runtime.ExecutablePath] = runtime;
        await host.Page.ActivateAsync();

        host.Dialogs.PickOpenFileResult = runtime.ExecutablePath;
        await host.Page.AddJavaCommand.ExecuteAsync(null);

        Assert.Single(host.Page.Runtimes);
        Assert.Contains("已在列表中", host.Page.StatusMessage);
    }

    [Fact]
    public async Task ValidateSelectedOnlyReverifiesTheSelectedRow()
    {
        using var host = new Host();
        var a = JavaTestRuntimes.Create(host.Home("jdk-a"));
        var b = JavaTestRuntimes.Create(host.Home("jdk-b"));
        host.Locator.Discovered.AddRange([a, b]);
        await host.Page.ActivateAsync();

        host.Page.SelectedRuntime = host.Page.Runtimes[0];
        await host.Page.ValidateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(3, host.Validator.ValidatedPaths.Count);
        Assert.Equal(a.ExecutablePath, host.Validator.ValidatedPaths[^1]);
        Assert.Contains("可用", host.Page.StatusMessage);
    }

    [Fact]
    public async Task ScanFailureAndTheEmptyStateAreReportedTruthfully()
    {
        using var host = new Host { FailRefresh = true };
        await host.Page.ActivateAsync();

        Assert.Empty(host.Page.Runtimes);
        Assert.False(host.Page.HasRuntimes);
        Assert.Contains("扫描 Java 运行时失败", host.Page.StatusMessage);
        Assert.False(string.IsNullOrWhiteSpace(host.Page.EmptyMessage));
    }

    [Fact]
    public async Task DisposedPageStopsScanningAndWriting()
    {
        using var host = new Host();
        host.Locator.Discovered.Add(JavaTestRuntimes.Create(host.Home("jdk17")));
        host.Page.Dispose();

        await host.Page.ActivateAsync();

        Assert.Equal(0, host.Locator.RefreshCalls);
        Assert.Empty(host.Page.Runtimes);
    }

    [Fact]
    public async Task SearchEntriesCoverTheRenderedSettingsAndCanBeHighlighted()
    {
        using var host = new Host();
        await host.Page.ActivateAsync();

        Assert.Equal(
            ["java.defaultPath", "java.runtimeProvider"],
            host.Page.SearchEntries.Select(entry => entry.OptionKey));

        Assert.True(host.Page.Highlight("java.runtimeProvider"));
        Assert.True(host.Page.Options.Single(option => option.Key == "java.runtimeProvider").IsHighlighted);
        Assert.Contains("已定位到", host.Page.StatusMessage);
    }

    private static async Task DrainAsync(SettingsJavaPageViewModel page)
    {
        for (var attempt = 0; attempt < 50 && page.IsBusy; attempt++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class Host : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Host()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-java-page-tests", Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.EnsureCreated();
            Directory.CreateDirectory(Paths.RuntimesDirectory);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(Paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            Settings = _provider.GetRequiredService<ISettingsService>();
            Locator = new FakeJavaLocator();
            Validator = new FakeJavaRuntimeValidator();
            Uninstall = new FakeJavaRuntimeUninstallService();
            Dialogs = new FakeDialogService();
            Registry = new JavaRuntimeRegistry(Paths, NullLogger<JavaRuntimeRegistry>.Instance);
            Page = new SettingsJavaPageViewModel(
                Locator,
                Registry,
                Validator,
                Uninstall,
                Settings,
                Dialogs,
                Paths,
                Localizer(),
                NullLogger<SettingsJavaPageViewModel>.Instance);
        }

        public string Root { get; }

        public AppPaths Paths { get; }

        public ISettingsService Settings { get; }

        public FakeJavaLocator Locator { get; }

        public FakeJavaRuntimeValidator Validator { get; }

        public FakeJavaRuntimeUninstallService Uninstall { get; }

        public FakeDialogService Dialogs { get; }

        public JavaRuntimeRegistry Registry { get; }

        public SettingsJavaPageViewModel Page { get; }

        public bool FailRefresh
        {
            init => Locator.FailRefresh = value;
        }

        public string Home(string name) => Path.Combine(Root, "java-homes", name);

        public void Dispose()
        {
            Page.Dispose();
            _provider.Dispose();
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
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public string? PickOpenFileResult { get; set; }

        public string LastError { get; private set; } = string.Empty;

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => ConfirmResult;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message) => LastError = message;

        public string? PickOpenFile(string title, string filter) => PickOpenFileResult;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
