// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区页面的数据上下文：持有该工作区自有的身份快照、实例级操作状态、
/// 运行设置与二级导航，并引用既有的实例与内容 ViewModel 提供命令与查询，
/// 不复制领域业务逻辑。
///
/// B2 步骤 1：页面身份（名称、版本、路径、创建/游玩时间）只读 <see cref="State"/>，
/// 不再读全局 <c>SelectedInstance</c>；B2 步骤 1c 起可编辑字段、模组内容与
/// 启动/健康检查/诊断等操作结果也由本页面自有对象持有，两个实例工作区同时打开时互不覆盖。
///
/// B2 步骤 2：二级导航逐项对应 <c>instance-workspace.md</c> §10，默认「基本信息」；
/// 尚未实现的页面只保留不可用导航项，不建空页（迁移规划 §5.4 B2 步骤 2）。
/// 关闭标签时 <see cref="Dispose"/> 释放这些状态与导航。
/// </summary>
public sealed partial class InstanceDetailPageViewModel : ObservableObject, IDisposable
{
    public InstanceDetailPageViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        ModsViewModel mods,
        InstanceRuntimeSettingsViewModel runtime,
        ILocalizer localizer)
    {
        State = state;
        Instances = instances;
        Mods = mods;
        Runtime = runtime;
        Operations = new InstanceOperationsViewModel(instances, state);
        BasicInfo = new InstanceBasicInfoViewModel(state, instances, runtime, Operations);

        Navigation = InstanceWorkspaceNavigationFactory.Create(
            localizer,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [InstanceWorkspacePageKeys.BasicInfo] = BasicInfo,
                [InstanceWorkspacePageKeys.Mods] = mods,
            });
    }

    public InstanceWorkspaceState State { get; }

    public InstancesViewModel Instances { get; }

    public ModsViewModel Mods { get; }

    /// <summary>本工作区自有的 Java/内存/JVM 编辑状态；不与其它实例或主页共享。</summary>
    public InstanceRuntimeSettingsViewModel Runtime { get; }

    /// <summary>本工作区自有的启动/健康检查/诊断/文件操作与结果状态。</summary>
    public InstanceOperationsViewModel Operations { get; }

    /// <summary>默认二级页「基本信息」。</summary>
    public InstanceBasicInfoViewModel BasicInfo { get; }

    /// <summary>§10 二级导航；切换只在工作区内换页。</summary>
    public InstanceWorkspaceNavigationViewModel Navigation { get; }

    /// <summary>
    /// 窄窗口下的布局收缩状态（ADR 0050 §响应式）：导航收为内容区顶部的页面选择器，
    /// 保证 1080×720 且导航与 Inspector 同时展开时，页面内容不被裁切。
    /// 由视图按可用宽度设置，属于呈现状态，不参与业务判断。
    /// </summary>
    [ObservableProperty]
    private bool _isNavigationCollapsed;

    public string InstanceId => State.InstanceId;

    public string InstanceName => State.Item.Name;

    public string InstanceMeta => State.Item.VersionText;

    /// <summary>
    /// 加载本页面的实例作用域状态（运行设置与模组内容）。
    /// 失败只影响本页文案，不构造空白页面；调用方负责记录异常。
    /// </summary>
    public Task InitializeAsync() =>
        Task.WhenAll(Runtime.InitializeAsync(), Mods.InitializeAsync());

    public void Dispose()
    {
        Navigation.Dispose();
        Operations.Dispose();
        Mods.Dispose();
        Runtime.Dispose();
        State.Dispose();
    }
}
