// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Deployment;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心的页面目录（B3 步骤 1）。
///
/// 现有元数据系统用 <c>CategoryKey</c> 表达“区块”，不等于 <c>settings.md</c> 的二级页；
/// 这里按 Approved 页面规格做一次呈现层映射：页面 → 分组 → 设置键。
/// 映射只决定“哪个键显示在哪一页”，不复制默认值、校验或持久化语义（ADR 0002）。
///
/// 未实现的分类不创建空页，只保留导航项和准确的不可用原因；
/// 规格要求但当前版本没有底层能力的项集中列在“尚未接入的能力”分组，不放置不生效的控件。
/// </summary>
public static class SettingsPageCatalog
{
    private sealed record GroupSpec(string TitleKey, string[] OptionKeys);

    private sealed record GapSpec(string TitleKey, string ReasonKey);

    private sealed record PageSpec(
        string Key,
        string TitleKey,
        string SubtitleKey,
        GroupSpec[] Groups,
        GapSpec[] Gaps,
        bool IncludesCodeSigning = false);

    /// <summary>
    /// D-01：Stable/Beta 是现有 main/dev 频道的展示名与别名。旧值（例如 "stable"）
    /// 在这里映射到规范选项，避免下拉框显示为空；用户重新选择后才写入规范值。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UpdateChannelAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stable"] = "main",
            ["beta"] = "dev",
        };

    private static readonly PageSpec[] Specs =
    [
        new(
            SettingsPageKeys.General,
            "settings.nav.general",
            "settings.page.general.subtitle",
            [
                new("settings.page.general.group.update", ["launcher.checkUpdates", "launcher.updateChannel"]),
                new("settings.page.general.group.actions", ["launcher.confirmBeforeDelete"]),
            ],
            [
                new("settings.page.general.gap.startup.title", "settings.page.general.gap.startup.reason"),
                new("settings.page.general.gap.window.title", "settings.page.general.gap.window.reason"),
                new("settings.page.general.gap.update.title", "settings.page.general.gap.update.reason"),
                new("settings.page.general.gap.notifications.title", "settings.page.general.gap.notifications.reason"),
                new("settings.page.general.gap.confirmations.title", "settings.page.general.gap.confirmations.reason"),
            ],
            IncludesCodeSigning: true),
        new(
            SettingsPageKeys.GameLaunch,
            "settings.nav.game",
            "settings.page.game.subtitle",
            [
                new("settings.page.game.group.memory", ["java.defaultMemoryMb"]),
                new("settings.page.game.group.jvm", ["java.extraArguments"]),
                new("settings.page.game.group.window", ["game.windowWidth", "game.windowHeight"]),
                new("settings.page.game.group.launch", ["game.showConsole", "game.extraArguments"]),
            ],
            [
                new("settings.page.game.gap.memoryPolicy.title", "settings.page.game.gap.memoryPolicy.reason"),
                new("settings.page.game.gap.fullscreen.title", "settings.page.game.gap.fullscreen.reason"),
            ]),
        new(
            SettingsPageKeys.Downloads,
            "settings.nav.downloads",
            "settings.page.downloads.subtitle",
            [
                new("settings.page.downloads.group.source", ["network.downloadSource"]),
                new("settings.page.downloads.group.concurrency", ["network.maxConcurrentDownloads"]),
            ],
            [
                new("settings.page.downloads.gap.retry.title", "settings.page.downloads.gap.retry.reason"),
                new("settings.page.downloads.gap.autoInstall.title", "settings.page.downloads.gap.autoInstall.reason"),
                new("settings.page.downloads.gap.limit.title", "settings.page.downloads.gap.limit.reason"),
            ]),
        new(
            SettingsPageKeys.Network,
            "settings.nav.network",
            "settings.page.network.subtitle",
            [
                new("settings.page.network.group.proxy", ["network.proxyMode", "network.proxyAddress"]),
            ],
            [
                new("settings.page.network.gap.endpoint.title", "settings.page.network.gap.endpoint.reason"),
                new("settings.page.network.gap.test.title", "settings.page.network.gap.test.reason"),
                new("settings.page.network.gap.default.title", "settings.page.network.gap.default.reason"),
            ]),
    ];

    /// <summary>
    /// 按 <c>settings.md</c> §3.2 的固定顺序构造二级导航。
    /// 只有已实现的分类提供页面对象；其余保留准确的不可用原因。
    /// </summary>
    public static SettingsNavigationViewModel CreateNavigation(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        CodeSigningTrustService codeSigning,
        SettingsJavaPageViewModel javaPage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(codeSigning);
        ArgumentNullException.ThrowIfNull(javaPage);

        var pages = new Dictionary<string, ISettingsPage>(StringComparer.Ordinal)
        {
            // Java 管理是独立页面对象（真实运行时清单），不是元数据分组页。
            [SettingsPageKeys.Java] = javaPage,
            // 界面偏好有字号档位与界面缩放的专用控件，同样不是通用元数据页。
            [SettingsPageKeys.Interface] = new SettingsInterfacePageViewModel(settings, localizer, dialogs),
        };
        foreach (var spec in Specs)
        {
            pages[spec.Key] = CreatePage(spec, settings, localizer, dialogs, codeSigning);
        }

        var items = new List<SettingsNavigationItemViewModel>
        {
            Item(SettingsPageKeys.General, "settings.nav.general"),
            Item(SettingsPageKeys.Interface, "settings.nav.interface"),
            Item(SettingsPageKeys.Java, "settings.nav.java"),
            Item(SettingsPageKeys.GameLaunch, "settings.nav.game"),
            Item(SettingsPageKeys.Downloads, "settings.nav.downloads"),
            Item(SettingsPageKeys.Network, "settings.nav.network"),
            Item(SettingsPageKeys.Storage, "settings.nav.storage", "settings.nav.unavailable.storage"),
            Item(SettingsPageKeys.Additions, "settings.nav.additions", "settings.nav.unavailable.additions"),
        };

        return new SettingsNavigationViewModel(items, SettingsPageKeys.General);

        SettingsNavigationItemViewModel Item(string key, string titleKey, string? unavailableKey = null)
        {
            pages.TryGetValue(key, out var page);
            return new SettingsNavigationItemViewModel(
                key,
                localizer.Get(titleKey),
                page,
                page is null && unavailableKey is not null
                    ? localizer.Get(unavailableKey)
                    : localizer.Get("settings.nav.unavailable.notImplemented"));
        }
    }

    private static ISettingsPage CreatePage(
        PageSpec spec,
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        CodeSigningTrustService codeSigning)
    {
        var options = new List<OptionEditorViewModel>();
        var sections = new List<object>();
        foreach (var group in spec.Groups)
        {
            var groupOptions = group.OptionKeys
                .Select(key => new OptionEditorViewModel(
                    settings.GetDefinition(key),
                    settings,
                    localizer,
                    instanceId: null,
                    key == "launcher.updateChannel" ? UpdateChannelAliases : null))
                .ToArray();
            options.AddRange(groupOptions);
            sections.Add(new SettingsOptionGroupViewModel(localizer.Get(group.TitleKey), groupOptions));
        }

        if (spec.IncludesCodeSigning)
        {
            sections.Add(new CodeSigningSectionViewModel(codeSigning, localizer, dialogs));
        }

        if (spec.Gaps.Length > 0)
        {
            sections.Add(new SettingsUnavailableSectionViewModel(
                localizer.Get("settings.page.gap.title"),
                spec.Gaps
                    .Select(gap => new SettingsUnavailableItemViewModel(
                        localizer.Get(gap.TitleKey),
                        localizer.Get(gap.ReasonKey)))
                    .ToArray()));
        }

        return new SettingsOptionsPageViewModel(
            spec.Key,
            localizer.Get(spec.TitleKey),
            localizer.Get(spec.SubtitleKey),
            sections,
            options,
            settings,
            localizer,
            dialogs);
    }
}
