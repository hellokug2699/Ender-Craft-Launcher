// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

internal sealed class SystemProcessLauncher(ILogger<SystemProcessLauncher> logger) : IProcessLauncher
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    public int Start(LaunchCommand command, string workingDirectory, bool showConsole, string instanceId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = !showConsole,
            RedirectStandardOutput = !showConsole,
            RedirectStandardError = !showConsole,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.Exited += (_, _) =>
        {
            logger.LogInformation(
                "实例 {InstanceId} 的游戏进程已退出，PID {Pid}，退出码 {ExitCode}。",
                instanceId,
                process.Id,
                process.ExitCode);
            _processes.TryRemove(process.Id, out _);
            process.Dispose();
        };

        if (!showConsole)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    logger.LogInformation("[MC] {Line}", eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    logger.LogWarning("[MC] {Line}", eventArgs.Data);
                }
            };
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("游戏进程启动失败。");
        }

        if (!showConsole)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        _processes[process.Id] = process;
        logger.LogInformation("已启动实例 {InstanceId} 的游戏进程，PID {Pid}。", instanceId, process.Id);
        return process.Id;
    }
}
