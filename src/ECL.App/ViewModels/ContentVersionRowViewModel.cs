// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;

namespace Ecl.App.ViewModels;

/// <summary>版本行在下载流程中的状态（`download-mods.md` §8）：白=进行中、绿=完成、红=失败。</summary>
public enum ContentVersionStatus
{
    /// <summary>尚未开始，不显示状态线。</summary>
    Idle,

    Queued,
    Downloading,
    Completed,
    Failed,
}

/// <summary>
/// 一个可下载版本的展示状态。行对象由页面按“来源 + 项目 + 版本”缓存，
/// 因此关闭详情弹窗不会丢失进度；重新打开同一项目的详情时仍显示真实状态。
/// </summary>
public sealed partial class ContentVersionRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public ContentVersionRowViewModel(
        ContentHit hit,
        ContentVersion version,
        bool canRequestDownload,
        string? downloadDisabledHint,
        ILocalizer localizer)
    {
        Hit = hit;
        Version = version;
        CanRequestDownload = canRequestDownload;
        DownloadDisabledHint = downloadDisabledHint;
        _localizer = localizer;

        VersionText = string.IsNullOrWhiteSpace(version.VersionName) ? version.VersionId : version.VersionName;
        GameVersionText = ContentDiscoveryPresentation.FormatVersionRange(version.GameVersions, localizer);
        SizeText = ContentDiscoveryPresentation.FormatSize(version.FileSize, localizer);
        DateText = ContentDiscoveryPresentation.FormatDate(version.PublishedAt, localizer);
        LoaderText = hit.Kind switch
        {
            ContentKind.Mod or ContentKind.ModPack when version.Loaders.Count > 0 =>
                string.Join(
                    " / ",
                    version.Loaders
                        .Select(ContentLoaders.DisplayName)
                        .Where(name => name.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
            ContentKind.Mod or ContentKind.ModPack => localizer.Get("downloads.discovery.unknown"),
            ContentKind.ShaderPack => ContentDiscoveryPresentation.FormatShaderApplicability(hit, localizer),
            _ => string.Empty,
        };
        FileNameText = version.FileName;
    }

    public ContentHit Hit { get; }

    public ContentVersion Version { get; }

    /// <summary>整合包部署模式尚未实现时，版本行不提供会失败的下载入口。</summary>
    public bool CanRequestDownload { get; }

    public string? DownloadDisabledHint { get; }

    public string VersionText { get; }

    public string GameVersionText { get; }

    public string LoaderText { get; }

    public string SizeText { get; }

    public string DateText { get; }

    public string FileNameText { get; }

    public string SourceLabel => Hit.SourceLabel;

    public bool ShowLoader => LoaderText.Length > 0;

    [ObservableProperty]
    private ContentVersionStatus _status;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    public bool ShowStatus => Status != ContentVersionStatus.Idle;

    public bool IsActive => Status is ContentVersionStatus.Queued or ContentVersionStatus.Downloading;

    public bool CanDownload => CanRequestDownload && !IsActive;

    public string DownloadHint => DownloadDisabledHint ?? _localizer.Get("downloads.discovery.download");

    /// <summary>无障碍名称带版本号，避免同一弹窗里多个下载按钮同名。</summary>
    public string DownloadAutomationName => $"{DownloadHint} {VersionText}";

    public void MarkQueued()
    {
        Status = ContentVersionStatus.Queued;
        StatusText = _localizer.Get("downloads.deploy.status.queued");
        ProgressText = string.Empty;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        NotifyState();
    }

    public void MarkDownloading()
    {
        Status = ContentVersionStatus.Downloading;
        StatusText = _localizer.Get("downloads.deploy.status.downloading");
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        NotifyState();
    }

    /// <summary>只映射领域真实上报的字节/文件进度；没有总数时不显示百分比。</summary>
    public void ApplyProgress(ContentProgress update)
    {
        Status = ContentVersionStatus.Downloading;
        StatusText = _localizer.Get("downloads.deploy.status.downloading");
        var hasBytes = update.TotalBytes > 0;
        IsProgressIndeterminate = !hasBytes && update.TotalFiles <= 0;
        ProgressValue = hasBytes
            ? Math.Clamp(update.CompletedBytes * 100d / update.TotalBytes, 0, 100)
            : update.TotalFiles > 0
                ? Math.Clamp(update.CompletedFiles * 100d / update.TotalFiles, 0, 100)
                : 0;
        ProgressText = hasBytes
            ? $"{update.Message} · {update.CompletedBytes * 100d / update.TotalBytes:0}%"
            : update.TotalFiles > 0
                ? $"{update.Message} · {update.CompletedFiles}/{update.TotalFiles}"
                : update.Message;
        NotifyState();
    }

    public void MarkCompleted(string detail)
    {
        Status = ContentVersionStatus.Completed;
        StatusText = _localizer.Get("downloads.deploy.status.completed");
        ProgressText = detail;
        ProgressValue = 100;
        IsProgressIndeterminate = false;
        NotifyState();
    }

    public void MarkFailed(string detail)
    {
        Status = ContentVersionStatus.Failed;
        StatusText = _localizer.Get("downloads.deploy.status.failed");
        ProgressText = detail;
        IsProgressIndeterminate = false;
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanDownload));
    }
}
