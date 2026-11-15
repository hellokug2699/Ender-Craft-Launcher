// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Net.Http;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.Download;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.Services;

/// <summary>只读更新检查的结论；页面只呈现这些结论，不推断更多信息。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>用户在设置 → 常规中关闭了更新检查。</summary>
    Disabled,

    /// <summary>缺少更新源、更新公钥等前置条件，还没有发起网络请求。</summary>
    Unavailable,

    /// <summary>已核对签名清单，当前版本不低于清单版本。</summary>
    UpToDate,

    /// <summary>已核对签名清单，清单版本高于当前部署版本。</summary>
    UpdateAvailable,

    /// <summary>已尝试检查（网络、签名或清单格式）但失败。</summary>
    Failed,
}

/// <summary>一次只读检查的真实结果。为空的可选字段表示该信息不可核实，页面应省略而不是补造。</summary>
public sealed record UpdateCheckReport(
    UpdateCheckOutcome Outcome,
    string Channel,
    string? CurrentVersion,
    string? AvailableVersion,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    string? ErrorCode,
    string? Message);

/// <summary>
/// 关于页「检查更新」的唯一入口（ADR 0051、维护者决策 E3-1）。
///
/// 只读取并校验远端签名清单，再与当前部署版本比较；不下载更新包、不替换程序文件、
/// 不启动 Bootstrapper，也不把打开 Releases 页面伪装成检查更新。
/// 没有可信更新源或公钥时返回明确不可用原因，绝不请求不安全的地址。
/// </summary>
public interface IUpdateCheckService
{
    Task<UpdateCheckReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 检查所需的进程外环境：安装目录（current.json / update-trust.json）与更新源地址。
/// 生产使用 <see cref="FromProcess"/>；测试注入临时目录，避免触碰真实安装。
/// </summary>
public sealed record UpdateCheckEnvironment(string BaseDirectory, string? Feed, string? TrustFile = null)
{
    public const string FeedEnvironmentVariable = "ECL_UPDATE_FEED";

    public static UpdateCheckEnvironment FromProcess() =>
        new(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(FeedEnvironmentVariable));

    public string ResolveTrustFile() =>
        string.IsNullOrWhiteSpace(TrustFile) ? Path.Combine(BaseDirectory, "update-trust.json") : TrustFile;
}

public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string CheckUpdatesKey = "launcher.checkUpdates";
    private const string ChannelKey = "launcher.updateChannel";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly IHttpClientProvider _http;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly UpdateCheckEnvironment _environment;

    public UpdateCheckService(
        ISettingsService settings,
        IHttpClientProvider http,
        ILogger<UpdateCheckService>? logger = null,
        UpdateCheckEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);

        _settings = settings;
        _http = http;
        _logger = logger ?? NullLogger<UpdateCheckService>.Instance;
        _environment = environment ?? UpdateCheckEnvironment.FromProcess();
    }

    public async Task<UpdateCheckReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var channel = ResolveChannel();
        if (!_settings.Get<bool>(CheckUpdatesKey))
        {
            return new UpdateCheckReport(
                UpdateCheckOutcome.Disabled,
                channel.ToApiValue(),
                CurrentVersionText(),
                null,
                null,
                null,
                null,
                null);
        }

        var feedValue = _environment.Feed;
        if (string.IsNullOrWhiteSpace(feedValue))
        {
            return Unavailable(
                channel,
                "update.feed_not_configured",
                "未配置更新源。");
        }

        if (!Uri.TryCreate(feedValue, UriKind.Absolute, out var feed) ||
            !string.Equals(feed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(channel, "update.insecure_feed", "更新源必须是 HTTPS 地址。");
        }

        var trust = await UpdateTrustStore
            .FromFileAsync(_environment.ResolveTrustFile(), cancellationToken)
            .ConfigureAwait(false);
        if (!trust.IsSuccess)
        {
            return Unavailable(channel, trust.Error!.Code, trust.Error.Message);
        }

        using var http = _http.CreateClient();
        http.Timeout = RequestTimeout;
        var client = new UpdateClient(http, new UpdateSignatureVerifier(trust.Value!));

        Result<UpdateCheckResult> check;
        try
        {
            check = await client.CheckAsync(feed, channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning(exception, "检查更新失败：{Feed}", feedValue);
            return Failed(channel, "update.check_failed", exception.Message);
        }

        if (!check.IsSuccess)
        {
            return Failed(channel, check.Error!.Code, check.Error.Message);
        }

        var manifest = check.Value!.Manifest;
        var current = await ResolveCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        var available = current is null || manifest.Version.CompareTo(current) > 0;
        return new UpdateCheckReport(
            available ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate,
            channel.ToApiValue(),
            current?.ToString(),
            manifest.Version.ToString(),
            manifest.ReleaseNotes,
            manifest.PublishedAt,
            null,
            null);
    }

    private UpdateChannel ResolveChannel()
    {
        var raw = _settings.Get<string>(ChannelKey);
        return UpdateChannels.TryParse(raw, out var channel) ? channel : UpdateChannel.Stable;
    }

    /// <summary>
    /// 当前部署版本以安装布局的 <c>current.json</c> 为准；开发构建或未部署时回退到程序集版本。
    /// 两者都不可解析时返回 null，页面按“当前版本未知”呈现，不猜测。
    /// </summary>
    private async Task<ReleaseVersion?> ResolveCurrentVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await new UpdateLayout(_environment.BaseDirectory)
                .ReadCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current.IsSuccess && current.Value!.ParsedVersion is { } version)
            {
                return version;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException)
        {
            _logger.LogInformation(exception, "无法读取安装布局的当前版本指针，回退到程序集版本。");
        }

        return ReleaseVersion.TryParse(BuildInfo.Version, out var fallback) ? fallback : null;
    }

    private string? CurrentVersionText() =>
        ReleaseVersion.TryParse(BuildInfo.Version, out var version) ? version.ToString() : BuildInfo.Version;

    private UpdateCheckReport Unavailable(UpdateChannel channel, string code, string message) =>
        new(
            UpdateCheckOutcome.Unavailable,
            channel.ToApiValue(),
            CurrentVersionText(),
            null,
            null,
            null,
            code,
            message);

    private UpdateCheckReport Failed(UpdateChannel channel, string code, string message) =>
        new(
            UpdateCheckOutcome.Failed,
            channel.ToApiValue(),
            CurrentVersionText(),
            null,
            null,
            null,
            code,
            message);
}
