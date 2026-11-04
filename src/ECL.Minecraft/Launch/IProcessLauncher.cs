// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Launch;

public interface IProcessLauncher
{
    /// <summary>
    /// 启动游戏进程。<paramref name="sessionId"/> 非空时，stdout/stderr 写入该会话目录，
    /// 并由进程监视器在退出后完成采集收尾与崩溃证据登记。
    /// </summary>
    int Start(
        LaunchCommand command,
        string workingDirectory,
        bool showConsole,
        string instanceId,
        string? sessionId = null);
}
