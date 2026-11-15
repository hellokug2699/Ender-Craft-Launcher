// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;
namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 按实例工作区创建「内容 · 资源包 / 光影包」页面状态（B2b）。
///
/// 与模组/修改页一致，每个工作区拥有自己的页面对象：两个实例同时打开时，
/// 列表、待应用更改与状态文案互不共享；关闭工作区时由
/// <see cref="InstanceDetailPageViewModel"/> 释放。
/// </summary>
public sealed class ContentPackPageFactory(
    IContentPackService packs,
    IDialogService dialogs,
    ILocalizer localizer,
    IShaderSupportService shaderSupport,
    IShaderHintStore shaderHints)
{
    /// <summary>
    /// 由 Shell 注入的下载入口：带入实例 ID 与资源类型，切到下载中心对应页签
    /// （resource-library-template.md §2「下载 xxx」）。
    /// </summary>
    public Action<string, ContentPackKind>? OpenPackBrowser { get; set; }

    public ContentPackPageViewModel Create(string instanceId, ContentPackKind kind) =>
        new(instanceId, kind, packs, dialogs, localizer, shaderSupport, shaderHints)
        {
            OpenPackBrowser = OpenPackBrowser,
        };
}
