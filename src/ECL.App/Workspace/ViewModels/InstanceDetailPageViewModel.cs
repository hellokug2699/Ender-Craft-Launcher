// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Storage;

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
/// B2 步骤 3b-4a：新增「修改」页，只生成只读变更计划与影响预览（受控执行属 3b-4b）。
/// 关闭标签时 <see cref="Dispose"/> 释放这些状态与导航。
/// </summary>
public sealed partial class InstanceDetailPageViewModel : ObservableObject, IDisposable
{
    public InstanceDetailPageViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        ModsViewModel mods,
        InstanceRuntimeSettingsViewModel runtime,
        StorageAnalysisService storageAnalysis,
        Action<string>? manageStorage,
        IMemoryPressureMonitor memory,
        InstanceModifyPageFactory modifyFactory,
        ContentPackPageFactory contentPackFactory,
        DataPackPageFactory dataPackFactory,
        ScreenshotPageFactory screenshotFactory,
        SchematicPageFactory schematicFactory,
        ExportPageFactory exportFactory,
        ILocalizer localizer)
    {
        State = state;
        Instances = instances;
        Mods = mods;
        Runtime = runtime;
        Operations = new InstanceOperationsViewModel(instances, state);
        Storage = new InstanceStorageViewModel(
            state.Model,
            storageAnalysis.MeasureInstanceAsync,
            manageStorage,
            localizer);
        BasicInfo = new InstanceBasicInfoViewModel(
            state,
            instances,
            runtime,
            Operations,
            Storage,
            memory,
            localizer);
        Modify = modifyFactory.Create(state);
        Modify.WorkspaceRefresh = RefreshAfterModifyAsync;
        ResourcePacks = contentPackFactory.Create(state.InstanceId, ContentPackKind.ResourcePack);
        Shaders = contentPackFactory.Create(state.InstanceId, ContentPackKind.ShaderPack);
        DataPacks = dataPackFactory.Create(state.InstanceId);
        Screenshots = screenshotFactory.Create(state.InstanceId);
        Schematics = schematicFactory.Create(state.InstanceId);
        Export = exportFactory.Create(state.InstanceId);

        Navigation = InstanceWorkspaceNavigationFactory.Create(
            localizer,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [InstanceWorkspacePageKeys.BasicInfo] = BasicInfo,
                [InstanceWorkspacePageKeys.Mods] = mods,
                [InstanceWorkspacePageKeys.ResourcePacks] = ResourcePacks,
                [InstanceWorkspacePageKeys.Shaders] = Shaders,
                [InstanceWorkspacePageKeys.DataPacks] = DataPacks,
                [InstanceWorkspacePageKeys.Modify] = Modify,
                [InstanceWorkspacePageKeys.Screenshots] = Screenshots,
                [InstanceWorkspacePageKeys.Schematics] = Schematics,
                [InstanceWorkspacePageKeys.Export] = Export,
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

    /// <summary>「配置 · 修改」二级页（B2 步骤 3b-4a：只读变更计划与影响预览）。</summary>
    public InstanceModifyViewModel Modify { get; }

    /// <summary>「内容 · 资源包」二级页（B2b：双表、拖动启用/禁用、应用）。</summary>
    public ContentPackPageViewModel ResourcePacks { get; }

    /// <summary>「内容 · 光影包」二级页（B2b：同一模板 + 光影支持检测与首次提示）。</summary>
    public ContentPackPageViewModel Shaders { get; }

    /// <summary>「内容 · 数据包」二级页（B2 收尾：世界目标 + 双表 + level.dat 应用）。</summary>
    public InstanceDataPacksViewModel DataPacks { get; }

    /// <summary>「世界与文件 · 截图」二级页（B2c：三种展示形式、页面内预览与受控删除）。</summary>
    public ScreenshotPageViewModel Screenshots { get; }

    /// <summary>「世界与文件 · 投影」二级页（B2c：列表式文件管理与真实支持检测）。</summary>
    public SchematicPageViewModel Schematics { get; }

    /// <summary>「世界与文件 · 导出」二级页（B2c：完整/自定义/诊断包，固定输出到实例 exports）。</summary>
    public InstanceExportViewModel Export { get; }

    /// <summary>当前实例的存储占用卡状态（§5）；由基本信息页展示。</summary>
    public InstanceStorageViewModel Storage { get; }

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
    /// 加载本页面的实例作用域状态（运行设置、模组内容与存储占用）。
    /// 失败只影响本页文案，不构造空白页面；调用方负责记录异常。
    /// </summary>
    public Task InitializeAsync() =>
        Task.WhenAll(
            Runtime.InitializeAsync(),
            Mods.InitializeAsync(),
            Storage.RefreshAsync(),
            ResourcePacks.InitializeAsync(),
            Shaders.InitializeAsync(),
            DataPacks.InitializeAsync(),
            Screenshots.InitializeAsync(),
            Schematics.InitializeAsync());

    /// <summary>
    /// 受控变更成功后重新读取实例并刷新工作区身份（B2 步骤 3b-4b）：
    /// 只替换实例列表中的目标项与页面身份快照，InstanceId 不变；基本信息的身份文本与
    /// Java 兼容性按新版本重新评估，存储占用重新统计。不重建工作区、不切换标签。
    /// </summary>
    internal async Task RefreshAfterModifyAsync()
    {
        if (State.IsDisposed)
        {
            return;
        }

        var item = await Instances.ReloadInstanceAsync(State.InstanceId).ConfigureAwait(true);
        if (item is null || State.IsDisposed)
        {
            // 实例记录已不存在：保留页面，但不再替换身份，避免伪造新配置。
            return;
        }

        State.ReplaceItem(item);
        BasicInfo.RefreshIdentity();
        Runtime.RebindInstance(item.Model);
        OnPropertyChanged(nameof(InstanceName));
        OnPropertyChanged(nameof(InstanceMeta));
        await Storage.RefreshAsync().ConfigureAwait(true);
    }

    public void Dispose()
    {
        Navigation.Dispose();
        Modify.Dispose();
        ResourcePacks.Dispose();
        Shaders.Dispose();
        DataPacks.Dispose();
        Screenshots.Dispose();
        Schematics.Dispose();
        Export.Dispose();
        BasicInfo.Dispose();
        Storage.Dispose();
        Operations.Dispose();
        Mods.Dispose();
        Runtime.Dispose();
        State.Dispose();
    }
}
