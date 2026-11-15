// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Minecraft.World;

namespace Ecl.App.ViewModels;

/// <summary>
/// 内容发现页的唯一构造点：五个分类共用同一页面类型，只替换分类与目标语义。
/// 每个分类一个页面对象，页面之间不共享搜索/筛选状态。
/// </summary>
public sealed class ContentDiscoveryPageFactory(
    IContentSearchService search,
    IContentVersionCatalog catalog,
    IContentInstallService install,
    IInstanceService instances,
    IVersionCatalog versionCatalog,
    ISettingsService settings,
    IDialogEngine dialogEngine,
    IDialogService dialogs,
    ILocalizer localizer,
    IShaderSupportService shaderSupport,
    IDatapackService datapacks)
{
    public IReadOnlyList<ContentDiscoveryViewModel> CreateAll() =>
    [
        Create(ContentKind.Mod),
        Create(ContentKind.ModPack),
        Create(ContentKind.ResourcePack),
        Create(ContentKind.ShaderPack),
        Create(ContentKind.DataPack),
    ];

    public ContentDiscoveryViewModel Create(ContentKind kind) => new(
        kind,
        search,
        catalog,
        install,
        instances,
        versionCatalog,
        settings,
        dialogEngine,
        dialogs,
        localizer,
        shaderSupport,
        datapacks);
}
