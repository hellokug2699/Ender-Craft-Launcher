// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration.Settings;

/// <summary>
/// 日志与诊断工作区的显示偏好键（F-09 S4/S8）。
///
/// 这些键由日志页工具栏直接切换，因此不进入通用设置编辑器（<c>IsHidden</c>）；
/// 但仍必须在选项注册表中登记，才能通过 <see cref="ISettingsService"/> 校验与持久化。
/// 显示偏好只改变阅读器的呈现与导航，不改变会话采集、日志写盘或证据归属。
/// </summary>
public static class LogDisplayKeys
{
    /// <summary>启动失败后自动打开日志工作区并定位该次会话（默认关闭，不夺走采集）。</summary>
    public const string AutoOpenOnFailure = "logs.autoOpenOnFailure";

    /// <summary>日志正文自动换行（只影响显示，不修改原始文本）。</summary>
    public const string AutoWrap = "logs.autoWrap";

    /// <summary>跟随最新内容；暂停跟随只暂停视图滚动。</summary>
    public const string Follow = "logs.follow";

    /// <summary>仅显示警告与错误。</summary>
    public const string OnlyWarningsAndErrors = "logs.onlyWarningsAndErrors";

    /// <summary>被隐藏的日志级别名（逗号分隔，<see cref="Microsoft.Extensions.Logging.LogLevel"/> 名称）。</summary>
    public const string HiddenLevels = "logs.hiddenLevels";

    public const char HiddenLevelsSeparator = ',';
}
