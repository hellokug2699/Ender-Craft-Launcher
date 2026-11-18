// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;

namespace Ecl.App.Services.Confirmation;

/// <summary>
/// 确认的语义等级。默认是 <see cref="Mandatory"/>，因为绝大多数现有调用点都是
/// “删除/覆盖/吊销”这类不可逆动作；只有明确标注意图的可逆操作才降级为 Ordinary。
/// </summary>
public enum ConfirmationKind
{
    /// <summary>强制确认：不受任何偏好影响，用户必须明确同意。</summary>
    Mandatory = 0,

    /// <summary>普通确认：可逆、低风险操作，可由 <c>launcher.confirmOrdinaryActions</c> 关闭。</summary>
    Ordinary = 1,
}

/// <summary>
/// 确认策略的唯一判断入口。安全边界：<see cref="ConfirmationKind.Mandatory"/> 永远返回 true，
/// 任何偏好都不能把删除托管数据、吊销信任之类的动作降级为静默执行。
/// </summary>
public interface IConfirmationPolicy
{
    bool RequiresConfirmation(ConfirmationKind kind);
}

/// <summary>
/// 读取 <c>launcher.confirmOrdinaryActions</c> 的默认实现。
/// 注意：<c>launcher.confirmBeforeDelete</c> 表达的是“删除范围选择”（仅移除记录 / 同时删除托管数据），
/// 不是普通的“确认/取消”，因此不能复用于这里。
/// </summary>
public sealed class ConfirmationPolicy(ISettingsService settings) : IConfirmationPolicy
{
    public bool RequiresConfirmation(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.Mandatory => true,
        ConfirmationKind.Ordinary => settings.Get<bool>(LauncherKeys.ConfirmOrdinaryActions),
        // 未知等级按强制处理：宁可多问一次，也不能静默执行。
        _ => true,
    };
}
