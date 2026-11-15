// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.World;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「内容 · 数据包」页面状态（B2 收尾）。
///
/// 与资源包/光影包页一致，每个工作区拥有自己的页面对象：世界选择、列表与待应用更改
/// 不与其他工作区共享；关闭工作区时由 <see cref="InstanceDetailPageViewModel"/> 释放。
/// 世界目标与 <c>level.dat</c> 读写全部由 <see cref="IDatapackService"/> 负责。
/// </summary>
public sealed class DataPackPageFactory(
    IDatapackService datapacks,
    IDialogService dialogs,
    ILocalizer localizer)
{
    /// <summary>
    /// 由 Shell 注入的下载入口：带入实例 ID，切到下载中心「数据包」页签
    /// （resource-library-template.md §2「下载 xxx」）。
    /// </summary>
    public Action<string>? OpenDatapackBrowser { get; set; }

    public InstanceDataPacksViewModel Create(string instanceId) =>
        new(instanceId, datapacks, dialogs, localizer)
        {
            OpenDatapackBrowser = OpenDatapackBrowser,
        };
}
