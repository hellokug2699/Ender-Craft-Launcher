// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区页面的数据上下文：只引用既有的实例与内容 ViewModel，
/// 不把实例业务状态复制到工作区模型里。
/// </summary>
public sealed record InstanceDetailPageViewModel(InstancesViewModel Instances, ModsViewModel Mods);
