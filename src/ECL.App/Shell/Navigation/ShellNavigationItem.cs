// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell;

/// <summary>
/// 全局导航目的地。导航只选择高层区域，不描述工作区布局；
/// 每个目的地对应一个工具入口，打开或复用其工作区。
/// </summary>
/// <param name="Key">稳定键，用于 ECL_START_PAGE 与程序内部跳转。</param>
/// <param name="ToolId">工作区工具标识。</param>
/// <param name="Title">用户可见名称（已本地化）。</param>
/// <param name="Group">导航分组标题（已本地化）。</param>
/// <param name="Page">页面 ViewModel；由 App.xaml 的 DataTemplate 映射为视图。</param>
public sealed record ShellNavigationItem(string Key, string ToolId, string Title, string Group, object Page);
