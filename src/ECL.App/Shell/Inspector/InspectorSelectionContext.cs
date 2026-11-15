// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell.Inspector;

/// <summary>Inspector 选择层中的一条只读事实（标签 + 值）。</summary>
/// <param name="Label">已本地化字段名。</param>
/// <param name="Value">已格式化的只读值；不得包含绝对路径或凭据。</param>
public sealed record InspectorSelectionField(string Label, string Value);

/// <summary>
/// Inspector 选择层的最小契约（ADR 0056 §3、迁移规划 §4.5、§5.11）。
///
/// Inspector 有两层：
/// <list type="bullet">
/// <item>工作区层：Shell 提供的宿主元数据（标题、工具、生命周期、目标资源等）；</item>
/// <item>选择层：页面报告的「当前选中对象」只读摘要 + 少量上下文动作。</item>
/// </list>
///
/// 约束：
/// <list type="bullet">
/// <item>选择层由 Shell 统一下发与清空，页面不得直接修改其他工作区的 Inspector；</item>
/// <item>选择层只承载摘要，不得变成第二个编辑面板；</item>
/// <item>切换活动工作区时选择层必须清空，避免把上一工作区的选中对象显示到新工作区。</item>
/// </list>
/// </summary>
/// <param name="Title">选中对象的显示名称。</param>
/// <param name="Subtitle">可选的一行补充说明（例如类型或来源）。</param>
/// <param name="Fields">只读字段；顺序即显示顺序。</param>
public sealed record InspectorSelectionContext(
    string Title,
    string? Subtitle,
    IReadOnlyList<InspectorSelectionField> Fields)
{
    public InspectorSelectionContext(string title)
        : this(title, null, [])
    {
    }
}
