// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Runtime;
using Ecl.Operations;

namespace Ecl.App.Services.Lifecycle;

/// <summary>
/// 关闭前的真实状态快照。界面据此决定“直接退出 / 提示仍在进行 / 需要确认”，
/// 而不是自己统计任务或猜测有没有未保存内容。
/// </summary>
public sealed record CloseReadiness(
    int ActiveOperations,
    int ActiveBackgroundWork,
    bool HasDirtyWorkspaces,
    bool HasUnsafeToAbort);

public interface ICloseReadinessProbe
{
    CloseReadiness Read();
}

/// <summary>
/// 只读探测：活动任务取自任务中心快照，后台工作取自生命周期协调器计数。
///
/// 工作区脏状态（未保存的编辑器改动）与“危险到不能中止”的判定尚未接入，
/// 因此恒为 false —— 这两个字段已经出现在契约里，UI 可以先接线，
/// 但领域层不会在没有真实数据源时伪造 true/false 之外的结论。
/// </summary>
public sealed class CloseReadinessProbe(IOperationStore operations, IApplicationLifecycle lifecycle)
    : ICloseReadinessProbe
{
    public CloseReadiness Read()
    {
        ArgumentNullException.ThrowIfNull(operations);

        var activeOperations = operations.Snapshots.Count(snapshot => !snapshot.IsTerminal);
        return new CloseReadiness(
            activeOperations,
            lifecycle.ActiveBackgroundWork,
            // TODO(F-12 后续切片)：接入工作区脏状态与不可安全中止的判定。
            HasDirtyWorkspaces: false,
            HasUnsafeToAbort: false);
    }
}
