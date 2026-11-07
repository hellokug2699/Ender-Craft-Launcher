// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Content;

namespace Ecl.App.ViewModels;

/// <summary>
/// 从实例工作区（模组/资源包/光影包页）或其它入口带入下载中心的搜索上下文
/// （`download-mods.md` §4、`download-resource-template.md` §5）。
/// <para>
/// 目标实例使用稳定 <see cref="InstanceId"/>，不依赖页面的临时选择状态；<see cref="GameVersion"/>
/// 与 <see cref="Loader"/> 是调用方读取到的实例事实，用于已有实例锁定。下载中心仍会重新读取实例列表，
/// 目标实例已被移除或失效时要求重新选择，不会静默退回自定义筛选。
/// </para>
/// </summary>
public sealed record ContentSearchContext(
    ContentKind Kind,
    string? InstanceId,
    string? GameVersion,
    string? Loader,
    string? Query,
    bool ExistingInstance)
{
    /// <summary>已有实例目标（模组/资源包/光影包）：版本与加载器来自该实例并锁定。</summary>
    public static ContentSearchContext ForExistingInstance(
        ContentKind kind,
        string instanceId,
        string? gameVersion,
        string? loader,
        string? query) =>
        new(kind, instanceId, gameVersion, loader, query, ExistingInstance: true);

    /// <summary>无实例的发现浏览（模组/整合包）：只按页面筛选条件搜索。</summary>
    public static ContentSearchContext ForDiscovery(ContentKind kind, string? query) =>
        new(kind, InstanceId: null, GameVersion: null, Loader: null, query, ExistingInstance: false);
}
