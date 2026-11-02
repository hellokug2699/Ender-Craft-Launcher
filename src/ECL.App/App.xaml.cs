// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Ecl.App.Localization;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.App.Views;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Logging;
using Ecl.Core.Storage;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecl.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        try
        {
            var paths = AppPaths.Create();

            // 工作区在建立 DI 容器之前装配：主工作区来自 config/workspace.json，
            // 旧版 launcher.minecraftDirectory 只作为一次性迁移输入。
            WorkspaceBootstrap.Apply(
                paths,
                SettingsBootstrap.ReadGlobalString(paths, "launcher.minecraftDirectory"));
            paths.EnsureCreated();

            var language = SettingsBootstrap.ReadGlobalString(paths, "launcher.language")
                           ?? CultureInfo.CurrentUICulture.Name;
            var culture = JsonLocalizer.Normalize(CultureInfo.GetCultureInfo(language));
            LocalizationProvider.Current = new JsonLocalizer(culture);

            var services = new ServiceCollection();
            services.AddEclLogging(paths, console: false);
            services.AddEclCore(paths, culture);
            services.AddEclConfiguration();
            services.AddEclDownload();
            services.AddEclAuth();
            services.AddEclJava();
            services.AddEclMinecraft();

            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton(new Ecl.Deployment.CodeSigningTrustService());
            services.AddSingleton<InstancesViewModel>();
            services.AddSingleton<StorageViewModel>();
            services.AddSingleton<ModsViewModel>();
            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<DownloadViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<JavaManagementViewModel>();
            services.AddSingleton<LogSessionsViewModel>();
            services.AddSingleton<IMemoryPressureMonitor, MemoryPressureMonitor>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<OperationCenterViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();

            _services = services.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<App>>();
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // ADR 0018：首次启动必须先确认 Minecraft 工作区，之后才允许创建实例或下载。
            var workspace = _services.GetRequiredService<IWorkspaceService>();
            if (!workspace.IsConfigured)
            {
                var setup = new WorkspaceSetupWindow(workspace, _services.GetRequiredService<ILocalizer>());
                setup.ShowDialog();
            }

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            splash.Close();

            _services.GetRequiredService<IMemoryPressureMonitor>().Start();

            // ADR 0020：启动时回收超过保留期且没有活动租约的 .part；清理失败不影响启动。
            _ = CleanupDownloadArtifactsAsync(_services, paths);
            await _services.GetRequiredService<MainViewModel>().InitializeAsync();
        }
        catch (Exception exception)
        {
            splash.Close();
            _logger?.LogCritical(exception, "启动失败。");
            MessageBox.Show(
                $"ECL 启动失败：{exception.Message}",
                "Ender Craft Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task CleanupDownloadArtifactsAsync(IServiceProvider services, AppPaths paths)
    {
        try
        {
            var artifacts = services.GetRequiredService<DownloadArtifactLifecycle>();
            var report = await artifacts.CleanupAsync(
                [paths.DownloadsDirectory, paths.CacheDirectory, paths.GameRoot],
                CancellationToken.None);
            _logger?.LogInformation(
                "下载临时产物清理：扫描 {Scanned}，删除 {Deleted}，释放 {Freed} 字节，活动保护 {Active}，保留 {Retained}，错误 {Errors}。",
                report.Scanned,
                report.Deleted,
                report.BytesFreed,
                report.ActiveSkipped,
                report.Retained,
                report.Errors.Count);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "下载临时产物清理失败；不影响启动与已完成下载。");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "未处理的 UI 异常。");
        MessageBox.Show(
            $"发生未处理的错误：{e.Exception.Message}",
            "Ender Craft Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
