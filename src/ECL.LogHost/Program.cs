// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Logging;
using Ecl.LogHost;
using Ecl.LogHost.Contracts;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var pipeName = LogHostProtocol.PipeName;
var idleSeconds = 300;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--pipe-name" when index + 1 < args.Length:
            pipeName = args[++index];
            break;
        case "--idle-seconds" when index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds):
            idleSeconds = Math.Max(5, seconds);
            index++;
            break;
        case "--help":
        case "-h":
            Console.WriteLine("""
                用法：ECL.LogHost.exe [--pipe-name <名称>] [--idle-seconds <秒>]

                独立日志采集宿主：创建游戏进程、持有 stdout/stderr 管道并写入按会话隔离的日志。
                主程序退出不会终止 LogHost；没有活动会话与客户端后按空闲时间退出。
                """);
            return 0;
        default:
            Console.Error.WriteLine($"未知参数：{args[index]}");
            return 2;
    }
}

// 同一用户只允许一个 LogHost 实例；重复启动直接成功返回，由已有实例继续服务。
using var instanceLock = new Mutex(initiallyOwned: true, $"ecl-loghost-{pipeName}", out var createdNew);
if (!createdNew)
{
    return 0;
}

var paths = AppPaths.Create();
paths.EnsureCreated();

var services = new ServiceCollection();
services.AddEclLogging(paths, console: false);
services.AddEclCore(paths);
services.AddEclSessions();
services.AddSingleton(provider => new LogHostServer(
    provider.GetRequiredService<IGameSessionService>(),
    provider.GetRequiredService<SessionArtifactCollector>(),
    provider.GetRequiredService<ILogger<LogHostServer>>(),
    pipeName));
services.AddSingleton<ILogger<LogHostServer>>(provider =>
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<LogHostServer>());

using var provider = services.BuildServiceProvider();
var server = provider.GetRequiredService<LogHostServer>();
var logger = provider.GetRequiredService<ILogger<LogHostServer>>();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var serverTask = server.RunAsync(shutdown.Token);
var idleTask = Task.Run(async () =>
{
    var idleSince = DateTimeOffset.UtcNow;
    while (!shutdown.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        if (server.IsShutdownRequested)
        {
            shutdown.Cancel();
            return;
        }

        if (server.Clients > 0 || server.ActiveSessions > 0)
        {
            idleSince = DateTimeOffset.UtcNow;
            continue;
        }

        if ((DateTimeOffset.UtcNow - idleSince).TotalSeconds >= idleSeconds)
        {
            logger.LogInformation("LogHost 空闲 {Seconds} 秒，准备退出。", idleSeconds);
            shutdown.Cancel();
            return;
        }
    }
});

try
{
    await serverTask.ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // 正常退出路径。
}

try
{
    await idleTask.ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // 正常退出路径。
}

provider.GetRequiredService<ILogger<LogHostServer>>().LogInformation("LogHost 已退出。");
return 0;
