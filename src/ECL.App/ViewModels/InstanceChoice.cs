// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.ViewModels;

/// <summary>
/// 通用实例选择项：稳定 InstanceId + 供界面显示的名称。
/// 工具中心等页面按需要把名称扩展成“名称 · 版本”，但持久化身份仍只使用 Id。
/// </summary>
public sealed record InstanceChoice(string Id, string Name);
