// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Ecl.Sessions;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「修改」页面状态（B2 步骤 3b-4a）。
///
/// 与模组页一样，每个工作区拥有自己的页面对象：两个实例工作区同时打开时，
/// 目标选择、计划与状态互不共享；关闭工作区时由
/// <see cref="InstanceDetailPageViewModel"/> 释放。
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
            localizer);
}
