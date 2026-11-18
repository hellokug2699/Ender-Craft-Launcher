// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Ecl.App.Dialogs;
using Ecl.App.Localization;
using Ecl.App.Services;
using Ecl.App.Services.Confirmation;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Services.Notifications;
using Ecl.App.Services.Updates;
using Ecl.App.Shell;
using Ecl.App.Shell.Inspector;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.Shell.Notifications;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Settings;
using Ecl.App.Views;
using Ecl.App.Workspace.ViewModels;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Logging;
using Ecl.Core.Runtime;
using Ecl.Core.Storage;
using Ecl.Core.Workspace;
using Ecl.Core.Workspace.Session;
using Ecl.Deployment;
using Ecl.Deployment.Startup;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ecl.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private MainWindowLifecycle? _mainWindowLifecycle;

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

            // 界面语言默认“跟随系统”（settings-interface-preferences.md §5）：
            // “system”与未设置都按操作系统首选语言解析，已保存的具体区域性名称原样使用。
            var language = SettingsBootstrap.ResolveLanguage(
                SettingsBootstrap.ReadGlobalString(paths, "launcher.language"),
                CultureInfo.CurrentUICulture);
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
            // 关于页的外部链接与只读更新检查：只读取并校验，不下载或替换程序文件。
            services.AddSingleton<IExternalLinkService, ExternalLinkService>();
            services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
            // F-12：登录启动注册、通知中心、更新调度、确认策略、关闭协调与会话恢复的唯一组合根。
            services.AddEclDeploymentStartup();
            // 领域服务允许缺省系统时钟；组合根显式登记一次，使所有时间相关服务共享同一来源。
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<INotificationPreferences, NotificationPreferences>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<OperationNotificationBridge>();
            services.AddSingleton<IUpdateCheckScheduler>(provider => new UpdateCheckScheduler(
                provider.GetRequiredService<ISettingsService>(),
                provider.GetRequiredService<IUpdateCheckService>(),
                provider.GetRequiredService<INotificationService>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<UpdateCheckScheduler>>(),
                provider.GetService<ILocalizer>()));
            services.AddSingleton<IConfirmationPolicy, ConfirmationPolicy>();
            // 结果弹窗复用统一弹窗引擎；宿主不可用时回退到兼容对话框，不吞掉结论。
            services.AddSingleton<IResultDialogService>(provider => new ResultDialogService(
                provider.GetRequiredService<ISettingsService>(),
                provider.GetRequiredService<IDialogEngine>(),
                provider.GetRequiredService<ILocalizer>(),
                provider.GetService<IDialogService>()));
            services.AddSingleton<ICloseReadinessProbe, CloseReadinessProbe>();
            // 构造即登记 "operations" 停止步骤；必须在关闭开始前解析（见 OnStartup）。
            services.AddSingleton<IAppShutdownCoordinator, AppShutdownCoordinator>();
            services.AddSingleton<IWorkspaceSessionStore, JsonWorkspaceSessionStore>();
            services.AddSingleton<IWorkspaceRestoreTarget, ShellWorkspaceRestoreTarget>();
            services.AddSingleton(provider => new WorkspaceRestoreService(
                provider.GetRequiredService<IWorkspaceSessionStore>(),
                provider.GetRequiredService<Ecl.Minecraft.Instances.IInstanceService>(),
                provider.GetRequiredService<IWorkspaceRestoreTarget>(),
                provider.GetRequiredService<ILogger<WorkspaceRestoreService>>()));
            services.AddSingleton<StartupRestorePrompt>();
            // F-12 托盘与窗口状态机：WinForms 类型只出现在实现文件，接口不泄漏。
            services.AddSingleton<ITrayHost, WinFormsNotifyIconTrayHost>();
            services.AddSingleton<IClosePrompt, ClosePromptService>();
            services.AddSingleton<NotificationHostViewModel>();
            services.AddSingleton<AutoStartSectionViewModel>();
            services.AddSingleton<SettingsGeneralPageViewModel>();
            services.AddSingleton<IShaderHintStore, ShaderHintStore>();
            // 主页中央实例切换栏的排列是主页布局偏好，保存在启动器数据目录，不写实例记录。
            services.AddSingleton<IHomeLayoutStore, HomeLayoutStore>();
            services.AddSingleton<IScreenshotImageLoader, ScreenshotImageLoader>();
            // 界面偏好（字体/字号档位/缩放/动效）在窗口显示前应用，避免首帧抖动。
            services.AddSingleton<IUserInterfacePreferences, UserInterfacePreferences>();
            services.AddSingleton(new Ecl.Deployment.CodeSigningTrustService());
            services.AddSingleton<InstancesViewModel>();
            services.AddSingleton<StorageViewModel>();
            services.AddSingleton<ModsPageFactory>();
            services.AddSingleton<InstanceModifyPageFactory>();
            services.AddSingleton<ContentPackPageFactory>();
            services.AddSingleton<DataPackPageFactory>();
            services.AddSingleton<ScreenshotPageFactory>();
            services.AddSingleton<SchematicPageFactory>();
            services.AddSingleton<ExportPageFactory>();
            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<InstanceVersionPageViewModel>();
            services.AddSingleton<ContentDiscoveryPageFactory>();
            services.AddSingleton<DownloadViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<Ecl.App.ViewModels.Settings.SettingsJavaPageViewModel>();
            services.AddSingleton<LogSessionsViewModel>();
            // 扩展生命周期与目录读取收敛到领域入口；设置 → 扩展管理页只做呈现与确认。
            services.AddSingleton<Ecl.App.Services.IAdditionsService, Ecl.App.Services.AdditionsService>();
            // 工具中心的原生条目只在 NativeToolsCenterCatalog 登记一次；工具内容仍是既有页面对象。
            services.AddSingleton<IToolsCenterCatalog, NativeToolsCenterCatalog>();
            services.AddSingleton<DiagnosticsViewModel>();
            services.AddSingleton<EngineToolsViewModel>();
            services.AddSingleton<ToolsViewModel>();
            services.AddSingleton<IMemoryPressureMonitor, MemoryPressureMonitor>();
            // F-03 §5.8：启动自检只报告需要恢复/可继续/可清理的生命周期状态，不自动执行。
            services.AddSingleton<LifecycleStartupCheck>();
            services.AddSingleton<AboutViewModel>();
            services.AddSingleton<OperationCenterViewModel>();
            // B11：安全目的地、两份页面与真实运行时状态数据源。
            services.AddSingleton<IRuntimeStatusService, RuntimeStatusService>();
            services.AddSingleton<Ecl.App.ViewModels.Security.RuntimeStatusViewModel>();
            services.AddSingleton<Ecl.App.ViewModels.Security.SecurityCenterViewModel>();
            services.AddSingleton<Ecl.App.ViewModels.Security.SecurityViewModel>();
            // B10：首次启动向导在 Shell / Dialog Engine 创建前运行；窗口只承载视图，状态在页面对象中。
            services.AddTransient<WorkspaceSetupViewModel>();

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
            // F-12：会话快照跟踪 Shell 的导航/工作区变化；退出前由宿主再显式保存一次。
            services.AddSingleton<WorkspaceSessionTracker>();
            // 窗口关闭/托盘状态机：退出时先走关闭协调器（生命周期 + 容器释放），再由宿主 Shutdown()。
            services.AddSingleton(provider =>
            {
                var tracker = provider.GetRequiredService<WorkspaceSessionTracker>();
                return new MainWindowLifecycle(
                    provider.GetRequiredService<MainWindow>(),
                    provider.GetRequiredService<ITrayHost>(),
                    provider.GetRequiredService<ISettingsService>(),
                    provider.GetRequiredService<IClosePrompt>(),
                    provider.GetRequiredService<ICloseReadinessProbe>(),
                    provider.GetRequiredService<IAppShutdownCoordinator>(),
                    provider.GetRequiredService<ILogger<MainWindowLifecycle>>(),
                    provider.GetService<ILocalizer>(),
                    provider.GetRequiredService<INotificationService>(),
                    prepareExit: () =>
                    {
                        tracker.Save();
                        return Task.CompletedTask;
                    },
                    exitHost: () =>
                    {
                        // 协调器已经释放容器，这里不再解析任何服务：只显式结束应用。
                        ExitHost();
                        return Task.CompletedTask;
                    });
            });

            _services = services.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<App>>();
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // D-03：旧配置的显式内存值迁移为“手动指定”，未修改过默认值的用户保持自动。
            // 迁移可能写一次全局配置；失败不阻断启动（运行时的兼容回退仍保留数值）。
            Ecl.Minecraft.Launch.MemoryPolicyMigration.TryMigrate(
                _services.GetRequiredService<ISettingsService>(),
                _logger);

            // D-04：旧版 network.proxyAddress 只作为迁移输入；可无歧义拆分时写入结构化键，
            // 无法拆分时保留原值并在网络页提示修复。失败不阻断启动（运行时会显式报错）。
            ProxySettingsMigration.TryMigrate(
                _services.GetRequiredService<ISettingsService>(),
                _services.GetRequiredService<IProxyCredentialStore>(),
                _logger);

            // ADR 0039：组合根建立后进入 Starting，初始化完成后再进入 Running。
            var lifecycle = _services.GetRequiredService<IApplicationLifecycle>();
            lifecycle.BeginStart();

            // ADR 0018：首次启动必须先确认 Minecraft 工作区，之后才允许创建实例或下载。
            var workspace = _services.GetRequiredService<IWorkspaceService>();
            if (!workspace.IsConfigured)
            {
                var setup = new WorkspaceSetupWindow(_services.GetRequiredService<WorkspaceSetupViewModel>());
                setup.ShowDialog();
            }

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;

            // F-12：主窗口的 Closing 由窗口状态机接管（询问/托盘/退出单飞）。
            // 关闭协调器在这里构造，确保在关闭开始前已登记 "operations" 停止步骤。
            var mainWindowLifecycle = _services.GetRequiredService<MainWindowLifecycle>();
            _mainWindowLifecycle = mainWindowLifecycle;
            _services.GetRequiredService<IAppShutdownCoordinator>();

            // ADR 0002 + settings-interface-preferences.md §7：偏好保存在配置服务里，
            // 启动时在显示窗口前应用一次；之后由设置变更事件自动重新应用。
            _services.GetRequiredService<IUserInterfacePreferences>().Attach(window);

            window.Show();
            splash.Close();

            // 登录启动项（--autostart）只决定“这次可以最小化”，是否真的隐藏仍由设置决定；
            // 手动启动时即使开着该设置也保持窗口可见，避免双击图标后找不到窗口。
            var launchedByAutoStart = e.Args.Any(argument =>
                string.Equals(argument, AutoStartRegistration.AutoStartArgument, StringComparison.OrdinalIgnoreCase));
            if (launchedByAutoStart &&
                _services.GetRequiredService<ISettingsService>().Get<bool>(LauncherKeys.StartMinimizedToTray))
            {
                mainWindowLifecycle.StartMinimizedToTray();
            }

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
            var provider = _services;

            // F-12：登录启动项与设置对齐；失败只提示与记日志，不阻断启动。
            ReconcileAutoStart(provider);
            // 通知桥在 MarkRunning 之后订阅：窗口已经显示，提示条有宿主可承载。
            _ = provider.GetRequiredService<OperationNotificationBridge>();
            var updateScheduler = provider.GetRequiredService<IUpdateCheckScheduler>();
            lifecycle.TrackWork(
                new BackgroundWorkDescriptor("update-check-scheduler", "App", BackgroundWorkKind.Periodic),
                token => updateScheduler.StartAsync(token));

            lifecycle.TrackWork(
                new BackgroundWorkDescriptor("lifecycle-recovery-check", "App", BackgroundWorkKind.Io),
                token => provider.GetRequiredService<LifecycleStartupCheck>().RunAsync(token));
            // F-09 S6：GUI 启动也执行会话恢复，让“UI 重启后重新连接会话”不只存在于 CLI。
            lifecycle.TrackWork(
                new BackgroundWorkDescriptor("session-recovery", "App", BackgroundWorkKind.Io),
                token => RecoverSessionsAsync(provider, token));

            var shell = provider.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            // F-12：启动恢复询问必须在 Shell 就绪后执行（恢复目标要能真正导航）。
            await RunStartupRestoreAsync(provider, shell);
            // 询问结束后才开始记录新快照，避免覆盖“上次退出”的会话。
            provider.GetRequiredService<WorkspaceSessionTracker>().EnableCapture();
        }
        catch (Exception exception)
        {
            splash.Close();
            _logger?.LogCritical(exception, "启动失败。");

            if (_services is not null)
            {
                // 启动失败时窗口可能已显示；先解除关闭守卫，否则 Shutdown(-1) 会被 Closing 取消。
                try
                {
                    _mainWindowLifecycle?.Dispose();
                }
                catch (Exception disposeGuardException)
                {
                    _logger?.LogDebug(disposeGuardException, "启动失败时解除窗口关闭守卫失败。");
                }

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

    /// <summary>
    /// F-09 S6：启动时收敛上次留下的会话状态（Running 但进程已消失 → Interrupted，
    /// 进程仍在 → 重连）。与 CLI <c>session recover</c> 使用同一幂等实现；失败不阻塞启动。
    /// </summary>
    private static async Task RecoverSessionsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            var sessions = services.GetRequiredService<Ecl.Sessions.IGameSessionService>();
            var report = await Task.Run(sessions.Recover, cancellationToken);
            if (report.Interrupted + report.Reattached + report.Unknown > 0)
            {
                logger.LogWarning(
                    "会话恢复：扫描 {Scanned}，中断 {Interrupted}，重连 {Reattached}，未知 {Unknown}。",
                    report.Scanned,
                    report.Interrupted,
                    report.Reattached,
                    report.Unknown);
            }
            else
            {
                logger.LogInformation("会话恢复：扫描 {Scanned}，没有需要处理的会话。", report.Scanned);
            }

            foreach (var warning in report.Warnings)
            {
                logger.LogWarning("会话恢复：{Warning}", warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭应用时取消；下次启动会再次执行同一幂等恢复。
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "会话恢复失败；不影响启动。");
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
    /// F-12：启动时让 HKCU Run 与 <c>launcher.startWithWindows</c> 对齐。
    /// 失败只记录并通过通知告知（设置 → 常规 可重新应用），绝不阻断启动。
    /// </summary>
    private static void ReconcileAutoStart(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            var result = services.GetRequiredService<AutoStartReconciler>().Reconcile();
            if (result.IsSuccess)
            {
                return;
            }

            logger.LogWarning(
                "登录启动项对齐失败：{Code} {Message}",
                result.Error?.Code,
                result.Error?.Message);

            try
            {
                var localizer = services.GetRequiredService<ILocalizer>();
                services.GetRequiredService<INotificationService>().Publish(new AppNotification(
                    $"autostart:reconcile:{result.Error?.Code ?? "failed"}",
                    AppNotificationCategory.TaskFailed,
                    AppNotificationSeverity.Warning,
                    localizer.Get("notification.autostart.failed.title"),
                    localizer.Get(
                        "notification.autostart.failed.message",
                        result.Error?.Message ?? string.Empty),
                    DateTimeOffset.UtcNow,
                    NavigationDestination: NotificationDestinations.Settings));
            }
            catch (Exception notificationException)
            {
                // 通知失败不能改变启动路径；日志已经记录了真实原因。
                logger.LogDebug(notificationException, "发布登录启动项失败提示失败。");
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "登录启动项对齐异常；不影响启动。");
        }
    }

    /// <summary>
    /// F-12 启动恢复询问：只在有效快照存在时询问；用户拒绝或没有快照时按
    /// <c>launcher.showHomeOnStartup</c> 决定落点。恢复目标由 Shell 导航实现。
    /// </summary>
    private static async Task RunStartupRestoreAsync(IServiceProvider services, ShellViewModel shell)
    {
        var logger = services.GetRequiredService<ILogger<App>>();

        // 恢复实例工作区前必须加载实例列表；InitializeAsync 是幂等的，重复进入页面不会二次扫描。
        try
        {
            await services.GetRequiredService<InstancesViewModel>().InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "加载实例列表失败；启动恢复可能无法打开实例工作区。");
        }

        WorkspaceRestoreResult? restored = null;
        try
        {
            restored = await services.GetRequiredService<StartupRestorePrompt>().RunAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "启动工作区恢复询问失败；按默认落点继续。");
        }

        if (restored is { Restored: > 0 })
        {
            // 已有恢复的工作区成为当前落点，不再把用户带回主页。
            return;
        }

        ApplyStartupLanding(services, shell);
    }

    /// <summary>
    /// 不恢复/无快照时的启动落点：
    /// - <c>ECL_START_PAGE</c> 是显式覆盖，优先于一切；
    /// - <c>showHomeOnStartup=true</c>（默认）回到主页；
    /// - 为 false 时回退到快照记录的导航目的地，其次才是上次打开的实例/固定目的地；
    /// - 都不可用时仍回到主页：不猜测用户想去哪个页面。
    /// </summary>
    private static void ApplyStartupLanding(IServiceProvider services, ShellViewModel shell)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ECL_START_PAGE")))
        {
            return;
        }

        if (services.GetRequiredService<ISettingsService>().Get<bool>(LauncherKeys.ShowHomeOnStartup))
        {
            shell.ActivateDestination("home");
            return;
        }

        var snapshot = services.GetRequiredService<IWorkspaceSessionStore>().Load();
        if (snapshot is null)
        {
            shell.ActivateDestination("home");
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.NavigationDestination) &&
            shell.ActivateDestination(snapshot.NavigationDestination))
        {
            return;
        }

        foreach (var entry in snapshot.Open)
        {
            if (!string.IsNullOrWhiteSpace(entry.InstanceId) &&
                shell.OpenInstanceWorkspace(entry.InstanceId))
            {
                return;
            }

            var key = string.IsNullOrWhiteSpace(entry.Destination) ? entry.ToolId : entry.Destination;
            if (!string.IsNullOrWhiteSpace(key) && shell.ActivateDestination(key))
            {
                return;
            }
        }

        shell.ActivateDestination("home");
    }

    /// <summary>
    /// 宿主退出：关闭协调器已经完成生命周期停止与容器释放，这里只把服务引用置空并显式结束应用。
    /// 置空是必要的：OnExit 的兜底释放不应再次触碰已经释放的容器。
    /// </summary>
    private void ExitHost()
    {
        _services = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常退出已在 MainWindowLifecycle → 关闭协调器中完成可等待关闭。
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
