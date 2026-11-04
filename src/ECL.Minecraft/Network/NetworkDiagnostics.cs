// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Network;

public enum NetworkStageStatus
{
    Success,
    Failed,
    Timeout,
    Skipped,
    NotApplicable,
}

public sealed record NetworkStageResult(
    string Stage,
    NetworkStageStatus Status,
    double? Milliseconds = null,
    string? Detail = null);

/// <summary>网络路径必须被明确标注；代理结果不得冒充直连结果（ADR 0044 §6）。</summary>
public sealed record NetworkPath(string Kind, Uri? ProxyUri = null)
{
    public static NetworkPath Direct { get; } = new("direct");

    public string DisplayName => ProxyUri is null ? "直连" : $"代理 {ProxyUri.Host}";
}

public sealed record NetworkTarget(string Id, string DisplayName, Uri Uri, string Category)
{
    public override string ToString() => $"{DisplayName} ({Uri.Host})";
}

public sealed record NetworkTargetResult(
    string TargetId,
    string DisplayName,
    string Host,
    NetworkPath Path,
    DateTimeOffset MeasuredAt,
    IReadOnlyList<NetworkStageResult> Stages,
    bool ReachedApplication,
    int? HttpStatusCode,
    string? Summary)
{
    public double? TotalMilliseconds => Stages
        .Where(stage => stage.Milliseconds is not null)
        .Sum(stage => stage.Milliseconds!.Value);
}

public sealed record NetworkDiagnosticsRun(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    NetworkPath Path,
    IReadOnlyList<NetworkTargetResult> Results);

public sealed record NetworkProbeOptions(
    TimeSpan Timeout,
    int Repetitions = 1,
    string UserAgent = "ECL-NetworkDiagnostics/1.0");

public interface INetworkProbe
{
    Task<NetworkTargetResult> ProbeAsync(
        NetworkTarget target,
        NetworkPath path,
        NetworkProbeOptions options,
        CancellationToken cancellationToken = default);
}

public interface INetworkDiagnosticsService
{
    IReadOnlyList<NetworkTarget> Targets { get; }

    Task<Result<NetworkDiagnosticsRun>> RunAsync(
        IReadOnlyList<string>? targetIds = null,
        NetworkPath? path = null,
        NetworkProbeOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<NetworkDiagnosticsRun>>> ListHistoryAsync(
        CancellationToken cancellationToken = default);

    Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 按需网络诊断（ADR 0044）。只做 DNS/TCP/TLS/HTTP 连通性与延迟检测，不做主动测速、
/// 不附加凭据、不常驻轮询、不上传结果。
/// </summary>
public sealed class HttpNetworkProbe : INetworkProbe
{
    public async Task<NetworkTargetResult> ProbeAsync(
        NetworkTarget target,
        NetworkPath path,
        NetworkProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        var stages = new List<NetworkStageResult>();
        var host = target.Uri.Host;
        var port = target.Uri.Port;
        var reached = false;
        int? statusCode = null;

        if (path.ProxyUri is not null)
        {
            stages.Add(new NetworkStageResult(
                "dns",
                NetworkStageStatus.NotApplicable,
                Detail: "代理路径下 DNS 由代理执行，未单独探测。"));
            stages.Add(new NetworkStageResult(
                "tcp",
                NetworkStageStatus.NotApplicable,
                Detail: "代理路径下 TCP 连接由代理执行，未单独探测。"));
            stages.Add(new NetworkStageResult(
                "tls",
                NetworkStageStatus.NotApplicable,
                Detail: "代理路径下 TLS 由代理执行，未单独探测。"));
        }
        else
        {
            var dnsWatch = Stopwatch.StartNew();
            try
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dnsCts.CancelAfter(options.Timeout);
                var addresses = await Dns.GetHostAddressesAsync(host, dnsCts.Token).ConfigureAwait(false);
                dnsWatch.Stop();
                stages.Add(new NetworkStageResult(
                    "dns",
                    addresses.Length > 0 ? NetworkStageStatus.Success : NetworkStageStatus.Failed,
                    dnsWatch.Elapsed.TotalMilliseconds,
                    addresses.Length > 0 ? string.Join(", ", addresses.Select(item => item.ToString()).Take(3)) : "无解析结果。"));
                if (addresses.Length == 0)
                {
                    return Finish(target, path, stages, false, null, "DNS 解析失败。");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                dnsWatch.Stop();
                stages.Add(new NetworkStageResult("dns", NetworkStageStatus.Timeout, dnsWatch.Elapsed.TotalMilliseconds, "DNS 超时。"));
                return Finish(target, path, stages, false, null, "DNS 解析超时。");
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException)
            {
                dnsWatch.Stop();
                stages.Add(new NetworkStageResult("dns", NetworkStageStatus.Failed, dnsWatch.Elapsed.TotalMilliseconds, exception.Message));
                return Finish(target, path, stages, false, null, "DNS 解析失败。");
            }

            var tcpWatch = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tcpCts.CancelAfter(options.Timeout);
                await client.ConnectAsync(host, port, tcpCts.Token).ConfigureAwait(false);
                tcpWatch.Stop();
                stages.Add(new NetworkStageResult("tcp", NetworkStageStatus.Success, tcpWatch.Elapsed.TotalMilliseconds));
                if (target.Uri.Scheme == Uri.UriSchemeHttps)
                {
                    var tlsWatch = Stopwatch.StartNew();
                    try
                    {
                        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                        using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        tlsCts.CancelAfter(options.Timeout);
                        await ssl.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions { TargetHost = host },
                            tlsCts.Token).ConfigureAwait(false);
                        tlsWatch.Stop();
                        stages.Add(new NetworkStageResult(
                            "tls",
                            NetworkStageStatus.Success,
                            tlsWatch.Elapsed.TotalMilliseconds,
                            ssl.SslProtocol.ToString()));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        tlsWatch.Stop();
                        stages.Add(new NetworkStageResult("tls", NetworkStageStatus.Timeout, tlsWatch.Elapsed.TotalMilliseconds, "TLS 握手超时。"));
                        return Finish(target, path, stages, false, null, "TCP 可达，但 TLS 握手超时。");
                    }
                    catch (Exception exception) when (exception is IOException or AuthenticationException)
                    {
                        tlsWatch.Stop();
                        stages.Add(new NetworkStageResult("tls", NetworkStageStatus.Failed, tlsWatch.Elapsed.TotalMilliseconds, exception.Message));
                        return Finish(target, path, stages, false, null, "TCP 可达，但 TLS 握手失败。");
                    }
                }
                else
                {
                    stages.Add(new NetworkStageResult("tls", NetworkStageStatus.NotApplicable, Detail: "HTTP 目标不使用 TLS。"));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcpWatch.Stop();
                stages.Add(new NetworkStageResult("tcp", NetworkStageStatus.Timeout, tcpWatch.Elapsed.TotalMilliseconds, "TCP 连接超时。"));
                return Finish(target, path, stages, false, null, "DNS 可达，但 TCP 连接超时。");
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                tcpWatch.Stop();
                stages.Add(new NetworkStageResult("tcp", NetworkStageStatus.Failed, tcpWatch.Elapsed.TotalMilliseconds, exception.Message));
                return Finish(target, path, stages, false, null, "DNS 可达，但 TCP 连接失败。");
            }
        }

        var httpWatch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                Proxy = path.ProxyUri is null ? null : new WebProxy(path.ProxyUri),
                ConnectTimeout = options.Timeout,
            };
            using var http = new HttpClient(handler) { Timeout = options.Timeout };
            using var request = new HttpRequestMessage(HttpMethod.Head, target.Uri);
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            httpWatch.Stop();
            statusCode = (int)response.StatusCode;
            reached = true;
            stages.Add(new NetworkStageResult(
                "http",
                NetworkStageStatus.Success,
                httpWatch.Elapsed.TotalMilliseconds,
                $"HTTP {statusCode} {response.ReasonPhrase}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            httpWatch.Stop();
            stages.Add(new NetworkStageResult("http", NetworkStageStatus.Timeout, httpWatch.Elapsed.TotalMilliseconds, "HTTP 请求超时。"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            httpWatch.Stop();
            stages.Add(new NetworkStageResult("http", NetworkStageStatus.Failed, httpWatch.Elapsed.TotalMilliseconds, exception.Message));
        }

        return Finish(
            target,
            path,
            stages,
            reached,
            statusCode,
            reached
                ? $"已到达应用层（HTTP {statusCode}）。"
                : "未取得 HTTP 响应；请结合各阶段结果判断。");
    }

    private static NetworkTargetResult Finish(
        NetworkTarget target,
        NetworkPath path,
        List<NetworkStageResult> stages,
        bool reached,
        int? statusCode,
        string summary) =>
        new(
            target.Id,
            target.DisplayName,
            target.Uri.Host,
            path,
            DateTimeOffset.UtcNow,
            stages,
            reached,
            statusCode,
            summary);
}

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    private const int MaxHistory = 50;

    private static readonly NetworkTarget[] DefaultTargets =
    [
        new("mojang", "Mojang 版本元数据", new Uri("https://launchermeta.mojang.com/"), "minecraft"),
        new("modrinth", "Modrinth API", new Uri("https://api.modrinth.com/"), "content"),
        new("curseforge", "CurseForge API", new Uri("https://api.curseforge.com/"), "content"),
        new("wiki", "Minecraft Wiki", new Uri("https://zh.minecraft.wiki/"), "knowledge"),
        new("mcmod", "MC 百科", new Uri("https://www.mcmod.cn/"), "knowledge"),
        new("github", "GitHub API", new Uri("https://api.github.com/"), "project"),
    ];

    private readonly INetworkProbe _probe;
    private readonly IAppPaths _paths;
    private readonly ILogger<NetworkDiagnosticsService> _logger;

    public NetworkDiagnosticsService(
        INetworkProbe probe,
        IAppPaths paths,
        ILogger<NetworkDiagnosticsService>? logger = null)
    {
        _probe = probe;
        _paths = paths;
        _logger = logger ?? NullLogger<NetworkDiagnosticsService>.Instance;
    }

    public IReadOnlyList<NetworkTarget> Targets => DefaultTargets;

    public async Task<Result<NetworkDiagnosticsRun>> RunAsync(
        IReadOnlyList<string>? targetIds = null,
        NetworkPath? path = null,
        NetworkProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new NetworkProbeOptions(TimeSpan.FromSeconds(6));
        path ??= NetworkPath.Direct;
        var selected = targetIds is null || targetIds.Count == 0
            ? DefaultTargets
            : DefaultTargets
                .Where(target => targetIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        if (selected.Length == 0)
        {
            return Result<NetworkDiagnosticsRun>.Fail(
                "network.unknown_target",
                "没有匹配的检测目标。");
        }

        var repetitions = Math.Clamp(options.Repetitions, 1, 3);
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<NetworkTargetResult>();
        foreach (var target in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var iteration = 0; iteration < repetitions; iteration++)
            {
                try
                {
                    results.Add(await _probe.ProbeAsync(target, path, options, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "网络诊断目标 {Target} 探测失败。", target.Id);
                    results.Add(new NetworkTargetResult(
                        target.Id,
                        target.DisplayName,
                        target.Uri.Host,
                        path,
                        DateTimeOffset.UtcNow,
                        [new NetworkStageResult("probe", NetworkStageStatus.Failed, Detail: exception.Message)],
                        false,
                        null,
                        "探测过程异常。"));
                }
            }
        }

        var run = new NetworkDiagnosticsRun(
            $"{startedAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32],
            startedAt,
            DateTimeOffset.UtcNow,
            path,
            results);
        var saved = await SaveHistoryAsync(run, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            _logger.LogWarning("保存网络诊断历史失败：{Error}", saved.Error);
        }

        return Result<NetworkDiagnosticsRun>.Ok(run);
    }

    public async Task<Result<IReadOnlyList<NetworkDiagnosticsRun>>> ListHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var path = HistoryPath;
        if (!File.Exists(path))
        {
            return Result<IReadOnlyList<NetworkDiagnosticsRun>>.Ok([]);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var history = JsonSerializer.Deserialize<List<NetworkDiagnosticsRun>>(json, EclJson.Default) ?? [];
            return Result<IReadOnlyList<NetworkDiagnosticsRun>>.Ok(history);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<NetworkDiagnosticsRun>>.Fail(
                "network.history_corrupt",
                "网络诊断历史损坏。",
                exception.Message);
        }
    }

    public Task<Result> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(HistoryPath))
            {
                File.Delete(HistoryPath);
            }

            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Fail("network.history_clear_failed", "清除网络诊断历史失败。", exception.Message));
        }
    }

    private string HistoryPath => Path.Combine(_paths.ConfigDirectory, "network-diagnostics", "history.json");

    private async Task<Result> SaveHistoryAsync(
        NetworkDiagnosticsRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            var history = new List<NetworkDiagnosticsRun>();
            if (File.Exists(HistoryPath))
            {
                var json = await File.ReadAllTextAsync(HistoryPath, cancellationToken).ConfigureAwait(false);
                history = JsonSerializer.Deserialize<List<NetworkDiagnosticsRun>>(json, EclJson.Default) ?? [];
            }

            history.Add(run);
            history = history.TakeLast(MaxHistory).ToList();
            var directory = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(directory);
            var temporary = $"{HistoryPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(history, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, HistoryPath, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Result.Fail("network.history_write_failed", "保存网络诊断历史失败。", exception.Message);
        }
    }
}
