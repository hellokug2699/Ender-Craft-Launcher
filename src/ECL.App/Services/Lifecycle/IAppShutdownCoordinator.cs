// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Services.Lifecycle;

/// <summary>
/// 应用关闭的唯一入口（F-12）。只做两件事：驱动生命周期协调器完成受管停止，
/// 然后释放服务容器。**不**调用 <c>Application.Current.Shutdown()</c>：
/// 进程退出时机仍由 UI 宿主决定，领域层不接管窗口生命周期。
/// </summary>
public interface IAppShutdownCoordinator
{
    bool IsShuttingDown { get; }

    /// <summary>幂等：并发/重复调用返回同一个关闭任务，服务容器只释放一次。</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
