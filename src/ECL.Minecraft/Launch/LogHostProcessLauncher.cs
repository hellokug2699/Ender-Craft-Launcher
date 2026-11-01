// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Core;
using Ecl.LogHost.Contracts;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public sealed class LogHostUnavailableException(string message) : Exception(message);

/// <summary>
/// 通过独立 LogHost 进程启动游戏：管道由 LogHost 持有，ECL 主进程退出不会中断采集。
/// 找不到或无法启动 LogHost 时抛出 <see cref="LogHostUnavailableException"/>，由调用方降级。
/// </summary>
internal sealed class LogHostProcessLauncher(
    IAppPaths paths,
    ILogger<LogHostProcessLauncher> logger) : IProcessLauncher
{
    public const string ExecutablePathEnvironmentVariable = "ECL_LOG_HOST_PATH";

    private static readonly SemaphoreSlim ConnectGate = new(1, 1);
    private static NamedPipeLogHostClient? _client;
    private static Process? _spawnedHost;

    public int Start(
        LaunchCommand command,
        string workingDirectory,
        bool showConsole,
        string instanceId,
        string? sessionId = null)
    {
        if (sessionId is null)
        {
            throw new LogHostUnavailableException("LogHost 启动需要会话 ID。");
        }

        var client = EnsureConnected();

        var result = client
            .StartProcessAsync(new LogHostStartRequest(
                sessionId,
                instanceId,
                command.Executable,
                workingDirectory,
                command.Arguments))
            .GetAwaiter()
            .GetResult();

        if (!result.Success || result.ProcessId is not { } pid)
        {
            throw new LogHostUnavailableException(result.Error ?? "LogHost 未返回进程 ID。");
        }

        logger.LogInformation("LogHost 已启动实例 {InstanceId} 的游戏进程，PID {Pid}。", instanceId, pid);
        return pid;
    }

    private NamedPipeLogHostClient EnsureConnected()
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        ConnectGate.Wait();
        try
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }

            _client?.Dispose();
            EnsureHostRunning();

            var client = new NamedPipeLogHostClient();
            try
            {
                client.ConnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                client.Dispose();
                throw new LogHostUnavailableException($"无法连接 LogHost：{exception.Message}");
            }

            _client = client;
            return client;
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    private void EnsureHostRunning()
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            throw new LogHostUnavailableException(
                "未找到 ECL.LogHost.exe；请使用完整安装包或在 ECL_LOG_HOST_PATH 中指定路径。");
        }

        if (_spawnedHost is { HasExited: false })
        {
            return;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(LogHostProtocol.PipeName);
        startInfo.Environment[AppPaths.DataDirectoryEnvironmentVariable] = paths.Root;

        _spawnedHost = Process.Start(startInfo)
                       ?? throw new LogHostUnavailableException("无法启动 ECL.LogHost.exe。");
        logger.LogInformation("已启动独立日志宿主，PID {Pid}。", _spawnedHost.Id);
    }

    private static string? ResolveExecutable()
    {
        var overridden = Environment.GetEnvironmentVariable(ExecutablePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var fileName = OperatingSystem.IsWindows() ? "ECL.LogHost.exe" : "ECL.LogHost";
        var candidate = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }
}

/// <summary>
/// 优先把游戏进程与管道交给 LogHost；不可用时降级为进程内采集，并在会话中明确记录降级事实。
/// </summary>
internal sealed class HybridProcessLauncher(
    LogHostProcessLauncher logHost,
    SystemProcessLauncher fallback,
    Ecl.Sessions.IGameSessionService sessions,
    ILogger<HybridProcessLauncher> logger) : IProcessLauncher
{
    public int Start(
        LaunchCommand command,
        string workingDirectory,
        bool showConsole,
        string instanceId,
        string? sessionId = null)
    {
        try
        {
            return logHost.Start(command, workingDirectory, showConsole, instanceId, sessionId);
        }
        catch (LogHostUnavailableException exception)
        {
            logger.LogWarning("LogHost 不可用，降级为进程内采集：{Message}", exception.Message);
            if (sessionId is not null)
            {
                sessions.SetCaptureFault(sessionId, $"LogHost 不可用，已降级为进程内采集：{exception.Message}");
            }

            return fallback.Start(command, workingDirectory, showConsole, instanceId, sessionId);
        }
    }
}
