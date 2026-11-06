// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Export;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「世界与文件 · 导出」页面（B2c）。
/// 每个工作区拥有自己的页面对象；关闭工作区时由 <see cref="InstanceDetailPageViewModel"/> 释放。
/// </summary>
public sealed class ExportPageFactory(
    IInstanceExportService exports,
    IDialogService dialogs,
    ILocalizer localizer)
{
    /// <summary>由 Shell 注入“打开导出所在文件夹”；为 <c>null</c> 时该入口禁用。</summary>
    public Action<string>? OpenFolder { get; set; }

    public InstanceExportViewModel Create(string instanceId) =>
        new(instanceId, exports, dialogs, localizer, OpenFolder);
}
