// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 工作区生命周期（ADR 0055 §4）。切换或隐藏工作区不是关闭；后台 Task 的生命周期独立于这些状态。
/// </summary>
public enum WorkspaceLifecycleState
{
    /// <summary>已登记身份，尚未开始初始化。</summary>
    Created,

    /// <summary>正在解析工具、检查兼容性并准备内容。</summary>
    Opening,

    /// <summary>界面可交互。</summary>
    Ready,

    /// <summary>保留状态但退到后台；可由宿主按资源策略选择。</summary>
    Suspended,

    /// <summary>Close Guard 已批准，正在清理文档、任务与运行时资源。</summary>
    Closing,

    /// <summary>已释放工作区资源。</summary>
    Disposed,

    /// <summary>承载该工作区的运行时崩溃。</summary>
    Crashed,

    /// <summary>工具缺失、被禁用或不兼容，无法打开。</summary>
    Unavailable,
}
