// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Security;

/// <summary>
/// 安全区域目的地（hints/ui/pages/security-center.md §2、runtime-status.md §2）：
/// 横向二级导航承载「安全中心」与「状态中心」；切换二级页不新开工作区、不改变工作区标题。
/// </summary>
public sealed partial class SecurityViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public SecurityViewModel(
        SecurityCenterViewModel securityCenter,
        RuntimeStatusViewModel runtimeStatus,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(securityCenter);
        ArgumentNullException.ThrowIfNull(runtimeStatus);
        ArgumentNullException.ThrowIfNull(localizer);

        SecurityCenter = securityCenter;
        RuntimeStatus = runtimeStatus;
        _localizer = localizer;

        Pages =
        [
            new SecurityPageItem(SecurityPageKeys.Center, _localizer.Get("security.nav.center"), securityCenter),
            new SecurityPageItem(SecurityPageKeys.RuntimeStatus, _localizer.Get("security.nav.runtimeStatus"), runtimeStatus),
        ];
        _selectedPage = Pages[0];
        _currentPage = securityCenter;
    }

    public SecurityCenterViewModel SecurityCenter { get; }

    public RuntimeStatusViewModel RuntimeStatus { get; }

    public IReadOnlyList<SecurityPageItem> Pages { get; }

    [ObservableProperty]
    private SecurityPageItem _selectedPage;

    [ObservableProperty]
    private object _currentPage;

    public string Title => _localizer.Get("security.title");

    public string Subtitle => _localizer.Get("security.subtitle");

    /// <summary>导航标题由视图直接读取；这里保留页面身份供测试与 UIA。</summary>
    public static class SecurityPageKeys
    {
        public const string Center = "security.center";
        public const string RuntimeStatus = "security.runtime-status";
    }

    /// <summary>进入安全区域时激活当前二级页；安全中心不会因此重扫或加载历史结果。</summary>
    public void Activate() => ActivatePage(CurrentPage);

    partial void OnSelectedPageChanged(SecurityPageItem value) => CurrentPage = value.Page;

    partial void OnCurrentPageChanged(object value) => ActivatePage(value);

    [RelayCommand]
    private void SelectPage(SecurityPageItem? page)
    {
        if (page is not null)
        {
            SelectedPage = page;
        }
    }

    private void ActivatePage(object page)
    {
        if (page is RuntimeStatusViewModel runtime)
        {
            _ = runtime.ActivateAsync();
        }
        else if (page is SecurityCenterViewModel center)
        {
            center.Activate();
        }
    }
}

/// <summary>安全区域内的一个二级页；Key 稳定、Title 已本地化。</summary>
public sealed record SecurityPageItem(string Key, string Title, object Page)
{
    public override string ToString() => Title;
}
