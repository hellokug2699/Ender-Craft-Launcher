// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 「修改」页的状态机（`instance-modify.md` §7）。本步骤（3b-4a）只实现只读计划相关的
/// 五种状态；执行中/成功/失败状态属于受控执行步骤（3b-4b），在能力接入前不会伪造。
/// </summary>
public enum InstanceModifyPlanState
{
    /// <summary>未检查：显示当前配置与目标配置，尚未生成变更计划。</summary>
    NotChecked,

    /// <summary>检查中：正在收集 Java、内容、任务与快照信息，不允许重复提交。</summary>
    Checking,

    /// <summary>可执行：所有强制检查通过。</summary>
    Executable,

    /// <summary>需要处理：存在可由用户解决的冲突或缺失项。</summary>
    NeedsAction,

    /// <summary>不可执行：存在无法安全继续的条件。</summary>
    NotExecutable,
}

/// <summary>
/// 影响预览的三档分类（`instance-modify.md` §5）：确定影响 / 可能影响 / 未知。
/// <see cref="Info"/> 表示与本次变更无关的事实陈述；<see cref="Warning"/> 表示需要用户处理。
/// </summary>
public enum ModifyPlanImpact
{
    Info,
    Certain,
    Possible,
    Unknown,
    Warning,
}

/// <summary>计划条目的类别；仅用于分组与呈现，不参与执行。批量执行时按类别显示真实来源。</summary>
public enum ModifyPlanItemKind
{
    Instance,
    Directory,
    Version,
    Loader,
    Components,
    JavaCurrent,
    JavaTarget,
    Mods,
    Packs,
    DataPacks,
    Downloads,
    RunningProcess,
    WriteTasks,
    Snapshot,
    Manual,
}

/// <summary>
/// 一条只读计划条目：标题、明细、影响分类与已本地化的影响徽标文本。
/// 明细只包含领域层能够证明的事实；未知情况必须写成「未知」，不得伪装为兼容。
/// </summary>
public sealed record ModifyPlanItemViewModel(
    ModifyPlanItemKind Kind,
    string Title,
    string Detail,
    ModifyPlanImpact Impact,
    string ImpactText);

/// <summary>计划状态与影响徽标的本地化映射（唯一实现，避免页面散落文案）。</summary>
public static class ModifyPlanPresentation
{
    public static string StateText(InstanceModifyPlanState state, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(state switch
        {
            InstanceModifyPlanState.Checking => "instance.modify.state.checking",
            InstanceModifyPlanState.Executable => "instance.modify.state.executable",
            InstanceModifyPlanState.NeedsAction => "instance.modify.state.needsAction",
            InstanceModifyPlanState.NotExecutable => "instance.modify.state.notExecutable",
            _ => "instance.modify.state.notChecked",
        });
    }

    public static string StateDetail(InstanceModifyPlanState state, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(state switch
        {
            InstanceModifyPlanState.Checking => "instance.modify.state.checkingDetail",
            InstanceModifyPlanState.Executable => "instance.modify.state.executableDetail",
            InstanceModifyPlanState.NeedsAction => "instance.modify.state.needsActionDetail",
            InstanceModifyPlanState.NotExecutable => "instance.modify.state.notExecutableDetail",
            _ => "instance.modify.state.notCheckedDetail",
        });
    }

    public static string ImpactText(ModifyPlanImpact impact, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(impact switch
        {
            ModifyPlanImpact.Certain => "instance.modify.impact.certain",
            ModifyPlanImpact.Possible => "instance.modify.impact.possible",
            ModifyPlanImpact.Unknown => "instance.modify.impact.unknown",
            ModifyPlanImpact.Warning => "instance.modify.impact.warning",
            _ => "instance.modify.impact.info",
        });
    }
}
