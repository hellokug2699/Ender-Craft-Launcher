// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;

namespace Ecl.App.Services.Confirmation;

/// <summary>
/// 结果弹窗的内容契约占位：具体的内容 ViewModel 与 XAML 由 UI 切片提供。
/// 领域层只负责“是否自动关闭”与结果语义，不定义视觉。
/// </summary>
public interface IResultDialogContent
{
}

/// <summary>
/// 一次结果展示请求。<see cref="IsCritical"/> 为 true 时永远不自动关闭：
/// 删除失败、数据损坏这类结论必须由用户确认阅读。
/// </summary>
public sealed record ResultDialogRequest(string Title, string Message, bool IsCritical);

/// <summary>
/// 结果弹窗入口（F-12）。返回真实 <see cref="DialogResult"/>：
/// 自动关闭以 <see cref="DialogOutcome.Closed"/> 结束，**不伪造 Primary**，
/// 因此调用方不能把“窗口自己关了”当成“用户确认了”。
/// </summary>
public interface IResultDialogService
{
    Task<DialogResult> ShowResultAsync(
        ResultDialogRequest request,
        CancellationToken cancellationToken = default);
}
