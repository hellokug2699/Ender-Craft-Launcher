// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Ecl.App.Dialogs;
using Ecl.App.Localization;
using Ecl.App.Services;
using Ecl.App.Shell;
using Ecl.App.Shell.Inspector;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels;
using Ecl.App.Views;
using Ecl.App.Workspace.ViewModels;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Logging;
using Ecl.Core.Runtime;
using Ecl.Core.Storage;
using Ecl.Core.Workspace;
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
    private bool _shutdownStarted;

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

            // Output 面板只读取应用级有界日志缓冲；文件日志与游戏进程日志仍由各自机制负责。
            var logBuffer = new AppLogBuffer();
            services.AddSingleton(logBuffer);
            services.AddLogging(builder => builder.AddProvider(logBuffer));

            services.AddEclCore(paths, culture);
            services.AddEclConfiguration();
            services.AddEclDownload();
            services.AddEclAuth();
            services.AddEclJava();
            services.AddEclMinecraft();

            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IShaderHintStore, ShaderHintStore>();
            services.AddSingleton<IScreenshotImageLoader, ScreenshotImageLoader>();
            services.AddSingleton(new Ecl.Deployment.CodeSigningTrustService());
            services.AddSingleton<InstancesViewModel>();
            services.AddSingleton<StorageViewModel>();
            services.AddSingleton<ModsPageFactory>();
            services.AddSingleton<InstanceModifyPageFactory>();
            services.AddSingleton<ContentPackPageFactory>();
            services.AddSingleton<ScreenshotPageFactory>();
            services.AddSingleton<SchematicPageFactory>();
            services.AddSingleton<ExportPageFactory>();
            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<InstanceVersionPageViewModel>();
            services.AddSingleton<ContentDiscoveryPageFactory>();
            services.AddSingleton<DownloadViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<JavaManagementViewModel>();
            services.AddSingleton<LogSessionsViewModel>();
            services.AddSingleton<AdditionsViewModel>();
            services.AddSingleton<WardrobeViewModel>();
            services.AddSingleton<DiagnosticsViewModel>();
            services.AddSingleton<EngineToolsViewModel>();
            services.AddSingleton<ToolsViewModel>();
            services.AddSingleton<IMemoryPressureMonitor, MemoryPressureMonitor>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<OperationCenterViewModel>();

            // Shell / Workspace Host / Inspector / Output / Dialog Engine
            // 弹窗层与输出面板绑定到创建主窗口的 UI Dispatcher；Core 保持 UI 无关。
            var uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            services.AddSingleton(uiDispatcher);
            services.AddSingleton<DialogLayerViewModel>();
            services.AddSingleton<IDialogEngine, DialogEngine>();
            services.AddSingleton<IWorkspaceHost>(provider => new WorkspaceHost(
                logger: provider.GetService<ILogger<WorkspaceHost>>()));
            services.AddSingleton<InspectorViewModel>();
            services.AddSingleton<OutputPanelViewModel>();
            services.AddSingleton<ShellViewModel>();
            services.AddSingleton<MainWindow>();

            _services = services.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<App>>();
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // ADR 0039：组合根建立后进入 Starting，初始化完成后再进入 Running。
            var lifecycle = _services.GetRequiredService<IApplicationLifecycle>();
            lifecycle.BeginStart();

            // ADR 0018：首次启动必须先确认 Minecraft 工作区，之后才允许创建实例或下载。
            var workspace = _services.GetRequiredService<IWorkspaceService>();
            if (!workspace.IsConfigured)
            {
                var setup = new WorkspaceSetupWindow(workspace, _services.GetRequiredService<ILocalizer>());
                setup.ShowDialog();
            }

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.Show();
            splash.Close();

            var memoryPressure = _services.GetRequiredService<IMemoryPressureMonitor>();
            memoryPressure.Start();
            lifecycle.RegisterStopStep("memory-pressure-monitor", _ =>
            {
                memoryPressure.Stop();
                return Task.CompletedTask;
            });

            // ADR 0020：启动时回收超过保留期且没有活动租约的 .part；清理失败不影响启动。
            // ADR 0039：原本 fire-and-forget 的清理任务登记为受管理后台工作。
            lifecycle.TrackWork(
                new BackgroundWorkDescriptor("download-artifact-cleanup", "App", BackgroundWorkKind.Io),
                token => CleanupDownloadArtifactsAsync(_services, paths, token));

            lifecycle.MarkRunning();
            await _services.GetRequiredService<ShellViewModel>().InitializeAsync();
        }
        catch (Exception exception)
        {
            splash.Close();
            _logger?.LogCritical(exception, "启动失败。");

            if (_services is not null)
            {
                try
                {
                    var lifecycle = _services.GetRequiredService<IApplicationLifecycle>();
                    var report = await lifecycle.FailStartupAsync(exception);
                    if (!report.IsClean)
                    {
                        _logger?.LogWarning("启动失败后的清理存在问题：{Issues}", string.Join("；", report.Issues));
                    }
                }
                catch (Exception cleanupException)
                {
                    // 清理失败不得掩盖最初的启动异常。
                    _logger?.LogError(cleanupException, "启动失败后的清理流程也失败了。");
                }

                try
                {
                    await _services.DisposeAsync();
                    _services = null;
                }
                catch (Exception disposeException)
                {
                    _logger?.LogError(disposeException, "释放服务容器失败。");
                }
            }

            MessageBox.Show(
                $"ECL 启动失败：{exception.Message}",
                "Ender Craft Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task CleanupDownloadArtifactsAsync(
        IServiceProvider services,
        AppPaths paths,
        CancellationToken cancellationToken)
    {
        try
        {
            var artifacts = services.GetRequiredService<DownloadArtifactLifecycle>();
            var report = await artifacts.CleanupAsync(
                [paths.DownloadsDirectory, paths.CacheDirectory, paths.GameRoot],
                cancellationToken);
            _logger?.LogInformation(
                "下载临时产物清理：扫描 {Scanned}，删除 {Deleted}，释放 {Freed} 字节，活动保护 {Active}，保留 {Retained}，错误 {Errors}。",
                report.Scanned,
                report.Deleted,
                report.BytesFreed,
                report.ActiveSkipped,
                report.Retained,
                report.Errors.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭时取消启动清理是预期行为。
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "下载临时产物清理失败；不影响启动与已完成下载。");
        }
    }

    /// <summary>
    /// WPF 的 Closing 事件本身不能被异步等待（ADR 0039 §7）：先取消本次关闭，等到关闭流程
    /// 完成后再调用 Shutdown()。重复触发由 <see cref="_shutdownStarted"/> 拦截。
    /// </summary>
    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        e.Cancel = true;

        try
        {
            if (_services is not null)
            {
                var lifecycle = _services.GetRequiredService<IApplicationLifecycle>();
                var report = await lifecycle.ShutdownAsync();
                if (!report.IsClean)
                {
                    _logger?.LogWarning(
                        "应用关闭完成但存在问题（{Count} 项）：{Issues}",
                        report.Issues.Count,
                        string.Join("；", report.Issues));
                }

                await _services.DisposeAsync();
                _services = null;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "关闭流程失败，仍将退出。");
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常 UI 关闭已在 OnMainWindowClosing 中完成可等待关闭。
        if (_services is not null)
        {
            try
            {
                _services.Dispose();
            }
            catch (Exception exception)
            {
                _logger?.LogError(exception, "退出时释放服务容器失败。");
            }

            _services = null;
        }

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
