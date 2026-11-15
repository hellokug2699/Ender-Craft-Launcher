// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Configuration.Settings;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;

namespace Ecl.Minecraft.Java;

/// <summary>
/// Java 运行时卸载的引用探测（页面规格 §4.2）：
/// - 实例显式固定的 Java（<c>instance.java.executablePath</c>）；
/// - 仍在使用该运行时的活动游戏会话。
///
/// 只读取已有记录，不修改实例设置、不结束进程、不猜测引用关系；
/// 无法证明已退出时按“仍在使用”阻断（宁可拒绝卸载，也不删除正在使用的运行时）。
/// </summary>
internal sealed class InstanceJavaRuntimeUsageProvider(
    IInstanceService instances,
    ISettingsService settings,
    IGameSessionService sessions) : IJavaRuntimeUsageProvider
{
    /// <summary>处于活动状态的游戏会话；进程退出后进入采集收尾，不再占用 Java 文件。</summary>
    private static readonly IReadOnlyList<GameSessionState> ActiveSessionStates =
    [
        GameSessionState.Preparing,
        GameSessionState.Starting,
        GameSessionState.Running,
    ];

    public async Task<IReadOnlyList<JavaUninstallBlocker>> FindBlockersAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var blockers = new List<JavaUninstallBlocker>();

        var allInstances = await instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var instance in allInstances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var configured = settings.Get<string?>("instance.java.executablePath", instance.Id);
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            if (JavaPriorityOrdering.Matches(runtime, configured))
            {
                blockers.Add(new JavaUninstallBlocker(
                    JavaUninstallBlockerKind.InstancePinned,
                    $"{instance.Name} ({instance.Id})"));
            }
        }

        foreach (var session in sessions.List())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = session.Manifest;
            if (string.IsNullOrWhiteSpace(manifest.JavaPath) ||
                !ActiveSessionStates.Contains(manifest.State) ||
                !JavaPriorityOrdering.Matches(runtime, manifest.JavaPath))
            {
                continue;
            }

            if (manifest.ProcessId is { } processId && !IsProcessAlive(processId))
            {
                // 会话记录存在但游戏进程已经退出：不再占用运行时文件。
                continue;
            }

            blockers.Add(new JavaUninstallBlocker(
                JavaUninstallBlockerKind.GameRunning,
                manifest.InstanceName ?? manifest.SessionId));
        }

        return blockers;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
