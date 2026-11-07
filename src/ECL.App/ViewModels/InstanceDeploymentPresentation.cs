// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Minecraft.Install;

namespace Ecl.App.ViewModels;

/// <summary>
/// 部署页的状态词汇（`download-instance-versions.md` §10）。颜色由视图按状态解析，
/// 文案必须同时出现，不能只依赖颜色表达成败。
/// </summary>
public static class InstanceDeploymentPresentation
{
    public const string GameItem = "game";

    public const string LoaderItem = "loader";

    public const string FabricApiItem = "fabricApi";

    public const string OptiFineItem = "optifine";

    /// <summary>整合包模式下的整合包归档下载项（`download-instance-versions.md` §5.4）。</summary>
    public const string ModpackItem = "modpack";

    /// <summary>下载项的状态文案；<see cref="DeploymentItemStatus.Idle"/> 表示尚未开始，不显示状态。</summary>
    public static string DescribeStatus(DeploymentItemStatus status, ILocalizer localizer) => status switch
    {
        DeploymentItemStatus.NotSelected => localizer.Get("downloads.deploy.status.notSelected"),
        DeploymentItemStatus.Queued => localizer.Get("downloads.deploy.status.queued"),
        DeploymentItemStatus.Downloading => localizer.Get("downloads.deploy.status.downloading"),
        DeploymentItemStatus.Completed => localizer.Get("downloads.deploy.status.completed"),
        DeploymentItemStatus.Failed => localizer.Get("downloads.deploy.status.failed"),
        _ => string.Empty,
    };

    /// <summary>
    /// 安装阶段归属的下载项。安装服务按 Manifest → Version → Loader → Processors → Client →
    /// Libraries → Assets → Logging → FabricApi → OptiFine 推进；游戏文件阶段分布在加载器前后，
    /// 因此不能按“阶段序号大于全部游戏阶段”判断完成，必须逐项使用本映射。
    /// </summary>
    public static IReadOnlyList<string> ItemsForStage(InstallStage stage) => stage switch
    {
        InstallStage.Manifest or InstallStage.Version or InstallStage.Client
            or InstallStage.Libraries or InstallStage.Assets or InstallStage.Logging => [GameItem],
        InstallStage.Loader or InstallStage.Processors => [LoaderItem],
        InstallStage.FabricApi => [FabricApiItem],
        InstallStage.OptiFine => [OptiFineItem],
        _ => [],
    };

    /// <summary>阶段的先后顺序；用于把已经越过的项标记为完成。</summary>
    public static int StageOrder(InstallStage stage) => stage switch
    {
        InstallStage.Manifest => 0,
        InstallStage.Version => 1,
        InstallStage.Loader => 2,
        InstallStage.Processors => 3,
        InstallStage.Client => 4,
        InstallStage.Libraries => 5,
        InstallStage.Assets => 6,
        InstallStage.Logging => 7,
        InstallStage.FabricApi => 8,
        InstallStage.OptiFine => 9,
        InstallStage.Completed => 10,
        _ => -1,
    };

    /// <summary>该下载项最后一个阶段；越过它之后再出现的阶段说明该项已完成。</summary>
    public static int LastStageOrder(string itemKey) => itemKey switch
    {
        GameItem => StageOrder(InstallStage.Logging),
        LoaderItem => StageOrder(InstallStage.Processors),
        FabricApiItem => StageOrder(InstallStage.FabricApi),
        OptiFineItem => StageOrder(InstallStage.OptiFine),
        _ => -1,
    };

    /// <summary>顶部实例版本页的部署状态文案。</summary>
    public static string DescribeState(InstanceDeploymentState state, ILocalizer localizer) => state switch
    {
        InstanceDeploymentState.Deploying => localizer.Get("downloads.deploy.state.deploying"),
        InstanceDeploymentState.Succeeded => localizer.Get("downloads.deploy.state.succeeded"),
        InstanceDeploymentState.Failed => localizer.Get("downloads.deploy.state.failed"),
        _ => localizer.Get("downloads.deploy.state.configuring"),
    };
}

/// <summary>单个下载项（游戏文件、加载器、Fabric API、OptiFine）的状态与大小。</summary>
public sealed partial class DeploymentItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public DeploymentItemViewModel(string key, string title, ILocalizer localizer)
    {
        Key = key;
        Title = title;
        _localizer = localizer;
        SizeText = localizer.Get("downloads.deploy.sizeUnknown");
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>该下载项是否参与本次部署；未参与时整行灰化并显示「未选择」。</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private DeploymentItemStatus _status = DeploymentItemStatus.Idle;

    public string StatusText => InstanceDeploymentPresentation.DescribeStatus(Status, _localizer);

    public bool ShowStatusText => Status != DeploymentItemStatus.Idle;

    public bool IsDimmed => !IsIncluded;

    partial void OnStatusChanged(DeploymentItemStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowStatusText));
    }

    partial void OnIsIncludedChanged(bool value) => OnPropertyChanged(nameof(IsDimmed));

    public void Reset()
    {
        Status = IsIncluded ? DeploymentItemStatus.Idle : DeploymentItemStatus.NotSelected;
    }

    public void MarkNotSelected()
    {
        IsIncluded = false;
        Status = DeploymentItemStatus.NotSelected;
    }

    public void MarkIdle()
    {
        IsIncluded = true;
        Status = DeploymentItemStatus.Idle;
    }

    public void MarkQueued()
    {
        if (!IsIncluded)
        {
            return;
        }

        Status = DeploymentItemStatus.Queued;
    }

    public void MarkDownloading()
    {
        if (!IsIncluded)
        {
            return;
        }

        Status = DeploymentItemStatus.Downloading;
    }

    public void MarkCompleted()
    {
        if (!IsIncluded)
        {
            return;
        }

        Status = DeploymentItemStatus.Completed;
    }

    public void MarkFailed()
    {
        if (!IsIncluded)
        {
            return;
        }

        Status = DeploymentItemStatus.Failed;
    }
}

public enum DeploymentItemStatus
{
    /// <summary>已配置但尚未开始下载；不显示状态线。</summary>
    Idle,

    /// <summary>本次部署不包含该项（例如原版没有加载器）。</summary>
    NotSelected,

    Queued,
    Downloading,
    Completed,
    Failed,
}

public enum InstanceDeploymentState
{
    Configuring,
    Deploying,
    Succeeded,
    Failed,
}
