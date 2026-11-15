// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Dialogs;

/// <summary>
/// 在错误的线程上访问弹窗宿主或同步弹窗 API 时抛出。
/// 弹窗层与所有绑定属性只属于创建它时的 WPF Dispatcher；后台线程必须使用
/// <see cref="IDialogEngine.ShowAsync"/>（会封送显示操作）或自行回到 UI 线程。
/// </summary>
public sealed class DialogThreadAffinityException(string message) : InvalidOperationException(message);
