// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 按实例创建模组页面状态（B2 步骤 1c）。
///
/// 模组页面从“全局单例 + 当前选中实例”改为“每个实例工作区一个显式实例上下文”：
/// 工作区打开时创建、关闭时释放，因此两个实例同时打开时各自的模组列表与状态互不覆盖。
/// 健康检查摘要只读、不需要页面状态，也在这里提供，避免 Shell 依赖某个具体页面实例。
/// </summary>
public sealed class ModsPageFactory(
    IModService mods,
    IModpackService modpacks,
    IModDependencyInstaller dependencies,
    IContentPackService contentPacks,
    IDialogService dialogs,
    ILocalizer localizer)
{
    /// <summary>由 Shell 注入的依赖搜索入口（跳转到下载中心并带上依赖 ID 与实例 ID）。</summary>
    public Action<string, string>? SearchDependency { get; set; }

    public ModsViewModel Create(string instanceId, string instanceName) =>
        new(
            mods,
            modpacks,
            dependencies,
            contentPacks,
            dialogs,
            localizer,
            instanceId,
            instanceName)
        {
            SearchDependency = SearchDependency,
        };

    /// <summary>启动前健康检查的模组摘要；不创建页面、不修改任何页面状态。</summary>
    public async Task<string> DescribeHealthAsync(string instanceId)
    {
        var result = await mods.ScanAsync(instanceId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            return localizer.Get("instances.healthModsFailed", result.Error?.Message ?? string.Empty);
        }

        var report = result.Value!;
        return report.IssueCount == 0
            ? localizer.Get("instances.healthModsOk", report.EnabledCount, report.DisabledCount)
            : localizer.Get(
                "instances.healthModsIssues",
                report.EnabledCount,
                report.DisabledCount,
                report.IssueCount,
                string.Join(
                    Environment.NewLine,
                    report.Issues.Take(12).Select(issue =>
                        $"{issue.FileName}: {issue.Kind} ({issue.RelatedModId ?? issue.Detail ?? issue.ModId ?? string.Empty})")));
    }
}
