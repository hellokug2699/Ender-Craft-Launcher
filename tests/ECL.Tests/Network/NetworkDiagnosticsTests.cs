// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecl.Minecraft.Network;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Network;

public sealed class NetworkDiagnosticsTests
{
    [Fact]
    public async Task RunStoresLocalHistoryAndCanClearIt()
    {
        using var environment = new TestEnvironment();
        var probe = new FakeProbe();
        var service = new NetworkDiagnosticsService(probe, environment.Paths);

        var run = await service.RunAsync(["mojang", "github"]);
        Assert.True(run.IsSuccess, run.Error?.ToString());
        Assert.Equal(2, run.Value!.Results.Count);
        Assert.All(run.Value.Results, result => Assert.Equal("直连", result.Path.DisplayName));

        var history = await service.ListHistoryAsync();
        Assert.True(history.IsSuccess);
        Assert.Single(history.Value!);

        var cleared = await service.ClearHistoryAsync();
        Assert.True(cleared.IsSuccess);
        Assert.Empty((await service.ListHistoryAsync()).Value!);
    }

    [Fact]
    public async Task ProxyPathIsLabeledAndNotReportedAsDirect()
    {
        using var environment = new TestEnvironment();
        var probe = new FakeProbe();
        var service = new NetworkDiagnosticsService(probe, environment.Paths);
        var proxy = new NetworkPath("proxy", new Uri("http://127.0.0.1:7890"));

        var run = await service.RunAsync(["mojang"], proxy);
        Assert.True(run.IsSuccess, run.Error?.ToString());
        Assert.Equal("proxy", run.Value!.Results[0].Path.Kind);
        Assert.Contains("代理", run.Value.Results[0].Path.DisplayName);
    }

    [Fact]
    public async Task UnknownTargetIsRejected()
    {
        using var environment = new TestEnvironment();
        var service = new NetworkDiagnosticsService(new FakeProbe(), environment.Paths);
        var run = await service.RunAsync(["does-not-exist"]);
        Assert.False(run.IsSuccess);
        Assert.Equal("network.unknown_target", run.Error?.Code);
    }

    [Fact]
    public async Task Http403CountsAsReachedApplicationLayer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                try
                {
                    _ = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
                    var response = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                    await stream.WriteAsync(response, cts.Token);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                    // 第一个连接可能是探测 TCP 阶段后立即关闭的空连接。
                }
            }
        }, cts.Token);

        try
        {
            var probe = new HttpNetworkProbe();
            var result = await probe.ProbeAsync(
                new NetworkTarget("local", "Local", new Uri($"http://127.0.0.1:{port}/"), "test"),
                NetworkPath.Direct,
                new NetworkProbeOptions(TimeSpan.FromSeconds(5)));
            Assert.True(
                result.ReachedApplication,
                string.Join("; ", result.Stages.Select(stage => $"{stage.Stage}={stage.Status}:{stage.Detail}")));
            Assert.Equal(403, result.HttpStatusCode);
            Assert.Contains(result.Stages, stage => stage.Stage == "http" && stage.Status == NetworkStageStatus.Success);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                await server;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // 测试收尾。
            }
        }
    }

    private sealed class FakeProbe : INetworkProbe
    {
        public Task<NetworkTargetResult> ProbeAsync(
            NetworkTarget target,
            NetworkPath path,
            NetworkProbeOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkTargetResult(
                target.Id,
                target.DisplayName,
                target.Uri.Host,
                path,
                DateTimeOffset.UtcNow,
                [
                    new NetworkStageResult("dns", NetworkStageStatus.Success, 1),
                    new NetworkStageResult("tcp", NetworkStageStatus.Success, 2),
                    new NetworkStageResult("tls", NetworkStageStatus.Success, 3),
                    new NetworkStageResult("http", NetworkStageStatus.Success, 4, "HTTP 200 OK"),
                ],
                true,
                200,
                "已到达应用层（HTTP 200）。"));
    }
}
