// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.App.Services.Updates;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.ViewModels;

/// <summary>
/// 关于页面（<c>hints/ui/pages/about.md</c>，ADR 0013 / 0049 / 0050 / 0051）。
///
/// - 版本、频道、构建时间与架构只来自真实运行时/构建元数据；不可核实的字段省略，不显示占位值。
/// - 「检查更新」调用 <see cref="IUpdateCheckScheduler"/> 的只读检查（ADR 0051 / E3-1），
///   与启动时的周期检查共用单飞与状态键；不下载或替换程序文件。
/// - 「项目协作」保持 ADR 0049 的原生项目中心语义：显示所需扩展与真实安装状态，
///   未安装时提供扩展管理入口，不跳转 GitHub 网页替代。
/// - 第三方清单只列出实际随包分发且许可证文本可核对的组件；Contributors 只用集体署名，
///   不虚构人数、头像或组织。
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>原生 GitHub 项目中心所属 Feature Addition。</summary>
    public const string ProjectCenterAdditionId = "ecl.github-project-center";

    private const string RepositoryUrl = "https://github.com/hellokug2699/Ender-Craft-Launcher";
    private const string DocumentationUrl = "https://github.com/hellokug2699/Ender-Craft-Launcher/tree/nightly/docs";
    private const string LicenseUrl = "https://github.com/hellokug2699/Ender-Craft-Launcher/blob/nightly/LICENSE";
    private const string ContributorsUrl = "https://github.com/hellokug2699/Ender-Craft-Launcher/graphs/contributors";
    private const string CheckUpdatesKey = "launcher.checkUpdates";
    private const string ChannelKey = "launcher.updateChannel";
    private const string FontLicenseFileName = "HarmonyOS_Sans_LICENSE.txt";

    private readonly IUpdateCheckScheduler _scheduler;
    private readonly ISettingsService _settings;
    private readonly IAdditionsService _additions;
    private readonly IExternalLinkService _links;
    private readonly ILocalizer _localizer;
    private readonly ILogger<AboutViewModel> _logger;

    public AboutViewModel(
        IUpdateCheckScheduler scheduler,
        ISettingsService settings,
        IAdditionsService additions,
        IExternalLinkService links,
        ILocalizer localizer,
        ILogger<AboutViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(localizer);

        _scheduler = scheduler;
        _settings = settings;
        _additions = additions;
        _links = links;
        _localizer = localizer;
        _logger = logger ?? NullLogger<AboutViewModel>.Instance;

        ProjectName = _localizer.Get("about.projectName");
        Tagline = _localizer.Get("about.subtitle");
        ProjectDescription = _localizer.Get("about.projectDescription");
        VersionText = BuildInfo.Version;
        // 只有运行时确实能准确提供的架构才显示；WPF 进程架构来自运行时信息。
        ArchitectureText = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "x86",
            Architecture.Arm => "ARM",
            _ => string.Empty,
        };
        BuildTimeText = BuildInfo.BuildTime ?? string.Empty;
        CopyrightText = typeof(AboutViewModel).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        LicenseName = _localizer.Get("about.license.name");
        ContributorsName = _localizer.Get("about.contributors.name");
        UpdateStatusText = _localizer.Get("about.update.idle");

        var fontLicensePath = Path.Combine(AppContext.BaseDirectory, "Fonts", FontLicenseFileName);
        ThirdPartyComponents.Add(new ThirdPartyComponentViewModel(
            _localizer.Get("about.thirdParty.font.name"),
            _localizer.Get("about.thirdParty.versionUnknown"),
            _localizer.Get("about.thirdParty.font.license"),
            _localizer.Get("about.thirdParty.font.copyright"),
            fontLicensePath,
            File.Exists(fontLicensePath)));

        Activate();
    }

    /// <summary>打开设置中心「扩展管理」的入口；由 Shell 注入，页面不自行导航。</summary>
    public Action? OpenAdditionsManagement { get; set; }

    public string ProjectName { get; }

    public string Tagline { get; }

    public string ProjectDescription { get; }

    public string VersionText { get; }

    public string ArchitectureText { get; }

    public string BuildTimeText { get; }

    public bool HasBuildTime => !string.IsNullOrWhiteSpace(BuildTimeText);

    public string LicenseName { get; }

    public string CopyrightText { get; }

    public string ContributorsName { get; }

    public ObservableCollection<ThirdPartyComponentViewModel> ThirdPartyComponents { get; } = [];

    /// <summary>页面级状态：外部链接或更新检查的失败原因；成功时清空。</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _channelText = string.Empty;

    [ObservableProperty]
    private string _updateStatusText;

    [ObservableProperty]
    private string _updateDetailText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _canCheckForUpdates = true;

    [ObservableProperty]
    private string _projectCenterStatusText = string.Empty;

    [ObservableProperty]
    private string _projectCenterRequirementText = string.Empty;

    [ObservableProperty]
    private string _projectCenterDetailText = string.Empty;

    public string CheckUpdatesText => IsCheckingUpdates
        ? _localizer.Get("about.update.checking")
        : _localizer.Get("about.update.check");

    /// <summary>检查进行中或用户关闭检查时按钮不可用，避免重复触发和注定失败的点击。</summary>
    public bool IsUpdateCheckButtonEnabled => CanCheckForUpdates && !IsCheckingUpdates;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool HasUpdateDetail => !string.IsNullOrEmpty(UpdateDetailText);

    /// <summary>
    /// 进入页面时刷新可离线读取的真实状态：更新频道、更新检查开关与原生项目中心的扩展安装状态。
    /// 不发起网络请求；检查更新只由用户显式操作触发。
    /// </summary>
    public void Activate()
    {
        // 先回放调度器最近一次结论（可能来自启动周期检查），再读取离线可见的开关/频道状态；
        // 这样进入页面不会重复触网，但也不会丢掉后台已经检查到的结果。
        if (_scheduler.LastReport is { } lastReport)
        {
            ApplyReport(lastReport);
        }

        RefreshChannel();
        RefreshProjectCenterState();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates || !CanCheckForUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        StatusMessage = string.Empty;
        UpdateStatusText = _localizer.Get("about.update.checking");
        UpdateDetailText = string.Empty;

        try
        {
            // 用户手动触发：忽略周期，但关闭检查时调度器仍会返回 Disabled 而不触网。
            var report = await _scheduler.CheckNowAsync(userInitiated: true).ConfigureAwait(true);
            if (report is null)
            {
                // 调度器把异常转成了日志；页面必须给出可重试的失败结论而不是停在“检查中”。
                UpdateStatusText = _localizer.Get("about.update.failedNoReport");
                UpdateDetailText = string.Empty;
                CanCheckForUpdates = true;
                OnPropertyChanged(nameof(HasUpdateDetail));
                return;
            }

            ApplyReport(report);
        }
        catch (Exception exception)
        {
            // 服务本身应返回 Failed；这里只是兜底，确保页面不会因为异常而没有结果。
            _logger.LogWarning(exception, "关于页更新检查失败。");
            UpdateStatusText = _localizer.Get("about.update.failed", exception.Message);
            UpdateDetailText = string.Empty;
            CanCheckForUpdates = true;
            OnPropertyChanged(nameof(HasUpdateDetail));
        }
        finally
        {
            IsCheckingUpdates = false;
            OnPropertyChanged(nameof(CheckUpdatesText));
        }
    }

    [RelayCommand]
    private void OpenRepository() => OpenExternal(RepositoryUrl);

    [RelayCommand]
    private void OpenDocumentation() => OpenExternal(DocumentationUrl);

    [RelayCommand]
    private void OpenLicense() => OpenExternal(LicenseUrl);

    [RelayCommand]
    private void OpenContributors() => OpenExternal(ContributorsUrl);

    [RelayCommand]
    private void OpenProjectCenterManagement() => OpenAdditionsManagement?.Invoke();

    [RelayCommand]
    private void OpenThirdPartyLicense(ThirdPartyComponentViewModel? component)
    {
        if (component is null)
        {
            return;
        }

        if (!component.HasLicenseText)
        {
            StatusMessage = _localizer.Get("about.thirdParty.missingLicense");
            OnPropertyChanged(nameof(HasStatusMessage));
            return;
        }

        OpenExternal(component.LicensePath);
    }

    private void ApplyReport(UpdateCheckReport report)
    {
        CanCheckForUpdates = true;
        switch (report.Outcome)
        {
            case UpdateCheckOutcome.Disabled:
                UpdateStatusText = _localizer.Get("about.update.disabled");
                UpdateDetailText = string.Empty;
                CanCheckForUpdates = false;
                break;
            case UpdateCheckOutcome.Unavailable:
                UpdateStatusText = _localizer.Get(
                    "about.update.unavailable",
                    report.Message ?? report.ErrorCode ?? string.Empty);
                UpdateDetailText = _localizer.Get("about.update.unavailableHint");
                break;
            case UpdateCheckOutcome.UpToDate:
                UpdateStatusText = _localizer.Get(
                    "about.update.upToDate",
                    report.CurrentVersion ?? VersionText);
                UpdateDetailText = string.Empty;
                break;
            case UpdateCheckOutcome.UpdateAvailable:
                UpdateStatusText = _localizer.Get(
                    "about.update.available",
                    report.AvailableVersion ?? string.Empty,
                    ChannelDisplay(report.Channel));
                UpdateDetailText = _localizer.Get("about.update.availableHint");
                break;
            case UpdateCheckOutcome.Failed:
                UpdateStatusText = _localizer.Get(
                    "about.update.failed",
                    report.Message ?? report.ErrorCode ?? string.Empty);
                UpdateDetailText = string.Empty;
                break;
            default:
                UpdateStatusText = _localizer.Get("about.update.idle");
                UpdateDetailText = string.Empty;
                break;
        }

        OnPropertyChanged(nameof(HasUpdateDetail));
        OnPropertyChanged(nameof(CheckUpdatesText));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckUpdatesText));
        OnPropertyChanged(nameof(IsUpdateCheckButtonEnabled));
    }

    partial void OnCanCheckForUpdatesChanged(bool value) =>
        OnPropertyChanged(nameof(IsUpdateCheckButtonEnabled));

    private void RefreshChannel()
    {
        var raw = _settings.Get<string>(ChannelKey);
        var value = string.IsNullOrWhiteSpace(raw) ? "main" : raw;
        ChannelText = _localizer.Get(
            "about.channelValue",
            ChannelDisplay(value),
            value);

        // 用户在设置中关闭检查后，页面不再提供会失败的按钮，并显示关闭原因。
        var enabled = _settings.Get<bool>(CheckUpdatesKey);
        if (!enabled)
        {
            CanCheckForUpdates = false;
            UpdateStatusText = _localizer.Get("about.update.disabled");
            UpdateDetailText = string.Empty;
            OnPropertyChanged(nameof(HasUpdateDetail));
            return;
        }

        if (!CanCheckForUpdates && string.IsNullOrWhiteSpace(UpdateDetailText))
        {
            // 重新开启检查后从“已关闭”恢复为空闲状态。
            CanCheckForUpdates = true;
            UpdateStatusText = _localizer.Get("about.update.idle");
        }
    }

    private string ChannelDisplay(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return string.Empty;
        }

        var key = $"option.launcher.updateChannel.{channel}";
        var label = _localizer.Get(key);
        // 未知值不猜测别名，直接显示原始配置值。
        return string.Equals(label, key, StringComparison.Ordinal) ? channel : label;
    }

    private void RefreshProjectCenterState()
    {
        ProjectCenterRequirementText = _localizer.Get(
            "about.links.collaboration.requirement",
            ProjectCenterAdditionId);
        ProjectCenterDetailText = _localizer.Get("about.links.collaboration.detail");

        try
        {
            var load = _additions.LoadState();
            if (load.IsCorrupt)
            {
                ProjectCenterStatusText = _localizer.Get("about.links.collaboration.state.corrupt");
                return;
            }

            var installed = load.Document.Additions.FirstOrDefault(addition =>
                string.Equals(addition.Id.Value, ProjectCenterAdditionId, StringComparison.Ordinal));
            ProjectCenterStatusText = installed is null
                ? _localizer.Get("about.links.collaboration.state.notInstalled")
                : installed.Enabled
                    ? _localizer.Get("about.links.collaboration.state.installed", installed.Version.ToString())
                    : _localizer.Get("about.links.collaboration.state.disabled", installed.Version.ToString());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "读取扩展安装记录失败。");
            ProjectCenterStatusText = _localizer.Get("about.links.collaboration.state.corrupt");
        }
    }

    private void OpenExternal(string target)
    {
        StatusMessage = string.Empty;
        if (!_links.Open(target))
        {
            StatusMessage = _localizer.Get("about.links.openFailed", target);
        }

        OnPropertyChanged(nameof(HasStatusMessage));
    }
}

/// <summary>
/// 第三方组件行：名称、版本、许可证、版权与可获取的许可证文本路径。
/// 只呈现实际核对过的数据；许可证文本缺失时按钮仍可见但明确报告缺失。
/// </summary>
public sealed class ThirdPartyComponentViewModel
{
    public ThirdPartyComponentViewModel(
        string name,
        string versionText,
        string licenseName,
        string copyright,
        string licensePath,
        bool hasLicenseText)
    {
        Name = name;
        VersionText = versionText;
        LicenseName = licenseName;
        Copyright = copyright;
        LicensePath = licensePath;
        HasLicenseText = hasLicenseText;
    }

    public string Name { get; }

    public string VersionText { get; }

    public string LicenseName { get; }

    public string Copyright { get; }

    public string LicensePath { get; }

    public bool HasLicenseText { get; }
}
