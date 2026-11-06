// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Schematics;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「世界与文件 · 投影」页面（B2c）。
/// 每个工作区拥有自己的页面对象；关闭工作区时由 <see cref="InstanceDetailPageViewModel"/> 释放。
/// </summary>
public sealed class SchematicPageFactory(
    ISchematicService schematics,
    ISchematicSupportService support,
    IDialogService dialogs,
    ILocalizer localizer)
{
    public SchematicPageViewModel Create(string instanceId) =>
        new(instanceId, schematics, support, dialogs, localizer);
}
