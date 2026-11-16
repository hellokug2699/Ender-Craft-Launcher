// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.Core.Localization;
using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Ecl.Sessions;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「修改」页面状态（B2 步骤 3b-4a 计划，3b-4b 受控执行）。
///
/// 与模组页一样，每个工作区拥有自己的页面对象：两个实例工作区同时打开时，
/// 目标选择、计划、执行结果与恢复入口互不共享；关闭工作区时由
/// <see cref="InstanceDetailPageViewModel"/> 释放（任务本身继续由领域生命周期服务与统一 Operation 承载）。
///
/// 执行链路由 <see cref="IInstanceReconfigureService"/> 提供计划与 journal；页面不再自建快照，
/// 也不直接调用安装服务。
/// </summary>
public sealed class InstanceModifyPageFactory(
    IVersionCatalog versionCatalog,
    ILoaderCatalog loaderCatalog,
    IVersionResolver versionResolver,
    IJavaSelectionService javaSelection,
    IModService mods,
    IContentPackService contentPacks,
    IGameSessionService sessions,
    IOperationService operations,
    IInstanceReconfigureService reconfigure,
    IInstanceSnapshotService snapshots,
    IDialogEngine dialogs,
    ILocalizer localizer)
{
    public InstanceModifyViewModel Create(InstanceWorkspaceState state) =>
        new(
            state,
            versionCatalog,
            loaderCatalog,
            versionResolver,
            javaSelection,
            mods,
            contentPacks,
            sessions,
            operations,
            reconfigure,
            snapshots,
            dialogs,
            localizer);
}
