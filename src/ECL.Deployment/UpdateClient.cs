// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Ecl.Core;

namespace Ecl.Deployment;

public sealed record UpdateCheckResult(
    UpdateManifest Manifest,
    Uri ManifestUri,
    Uri SignatureUri,
    Uri PackageUri);

public static class UpdateFeed
{
    public const string OfficialBaseAddress = "https://updates.endercraftlauncher.top/";

    public static Uri GetManifestUri(Uri feedBase, UpdateChannel channel) =>
        new(NormalizeBase(feedBase), $"{channel.ToApiValue()}/update-manifest.json");

    public static Uri GetSignatureUri(Uri feedBase, UpdateChannel channel) =>
        new(NormalizeBase(feedBase), $"{channel.ToApiValue()}/update-manifest.sig");

    public static Uri GetPackageUri(Uri manifestUri, UpdatePackage package) =>
        Uri.TryCreate(package.FileName, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(manifestUri, package.FileName);

    private static Uri NormalizeBase(Uri feedBase) =>
        feedBase.AbsoluteUri.EndsWith('/') ? feedBase : new Uri(feedBase.AbsoluteUri + "/");
}

/// <summary>
/// 更新源客户端。只允许 HTTPS；先验证 detached signature，再解析 Manifest；
/// 下载发布包后再次校验长度与 SHA-256。
/// </summary>
public sealed class UpdateClient(HttpClient http, UpdateSignatureVerifier verifier)
{
    public static HttpClient CreateNoRedirectHttpClient(TimeSpan? timeout = null) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;

    public async Task<Result<UpdateCheckResult>> CheckAsync(
        Uri feedBase,
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var manifestUri = UpdateFeed.GetManifestUri(feedBase, channel);
        var signatureUri = UpdateFeed.GetSignatureUri(feedBase, channel);
        if (manifestUri.Scheme != Uri.UriSchemeHttps ||
            signatureUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result<UpdateCheckResult>.Fail(
                "update.insecure_feed",
                "更新源必须使用 HTTPS。");
        }

        var signature = await GetBytesAsync(signatureUri, MaxSignatureBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!signature.IsSuccess)
        {
            return Result<UpdateCheckResult>.Fail(signature.Error!);
        }

        var manifestBytes = await GetBytesAsync(manifestUri, MaxManifestBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!manifestBytes.IsSuccess)
        {
            return Result<UpdateCheckResult>.Fail(manifestBytes.Error!);
        }

        var verified = verifier.Verify(manifestBytes.Value!, signature.Value!);
        if (!verified.IsSuccess)
        {
            // A Pages deployment can switch between the two independent GETs.
            // Retry the pair once so a transient old-signature/new-Manifest mix
            // does not make an otherwise healthy channel appear permanently broken.
            signature = await GetBytesAsync(signatureUri, MaxSignatureBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!signature.IsSuccess)
            {
                return Result<UpdateCheckResult>.Fail(signature.Error!);
            }

            manifestBytes = await GetBytesAsync(manifestUri, MaxManifestBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!manifestBytes.IsSuccess)
            {
                return Result<UpdateCheckResult>.Fail(manifestBytes.Error!);
            }

            verified = verifier.Verify(manifestBytes.Value!, signature.Value!);
            if (!verified.IsSuccess)
            {
                return Result<UpdateCheckResult>.Fail(verified.Error!);
            }
        }

        var parsed = UpdateManifestParser.Parse(manifestBytes.Value!);
        if (!parsed.IsSuccess)
        {
            return Result<UpdateCheckResult>.Fail(parsed.Error!);
        }

        if (parsed.Value!.Channel != channel)
        {
            return Result<UpdateCheckResult>.Fail(
                "update.channel_mismatch",
                "更新清单的频道与当前请求频道不一致。");
        }

        return Result<UpdateCheckResult>.Ok(new UpdateCheckResult(
            parsed.Value!,
            manifestUri,
            signatureUri,
            UpdateFeed.GetPackageUri(manifestUri, parsed.Value!.Package)));
    }

    public async Task<Result> DownloadPackageAsync(
        UpdateManifest manifest,
        Uri packageUri,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (packageUri.Scheme != Uri.UriSchemeHttps ||
            (manifest.Package.IsAbsoluteUrl &&
             (!Uri.TryCreate(manifest.Package.FileName, UriKind.Absolute, out var declaredPackageUri) ||
              declaredPackageUri != packageUri ||
              !string.Equals(packageUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))))
        {
            return Result.Fail("update.insecure_package", "更新包必须使用受信任的 HTTPS 地址。");
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = destination + ".part";
        try
        {
            using var request = CreateRequest(packageUri);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 ||
                response.RequestMessage?.RequestUri is { } finalUri && finalUri != packageUri)
            {
                return Result.Fail("update.package_redirect_rejected", "更新包下载发生未授权重定向，已拒绝。");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Result.Fail(
                    "update.package_download_failed",
                    "下载更新包失败。",
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != manifest.Package.Size)
            {
                return Result.Fail(
                    "update.package_size_mismatch",
                    "更新包大小与清单不符。",
                    $"期望 {manifest.Package.Size}，实际 {contentLength}。");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, 1024 * 128, cancellationToken).ConfigureAwait(false);
            }

            var verification = await UpdatePackageVerifier
                .VerifyAsync(temporary, manifest.Package, cancellationToken)
                .ConfigureAwait(false);
            if (!verification.IsSuccess)
            {
                return verification;
            }

            File.Move(temporary, destination, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or HttpRequestException or CryptographicException)
        {
            return Result.Fail(
                "update.package_download_failed",
                "下载更新包失败。",
                exception.Message);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 清理失败不影响主结果。
                }
            }
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EnderCraftLauncher", "0.9.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    private async Task<Result<byte[]>> GetBytesAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri);
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 ||
            response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
        {
            return Result<byte[]>.Fail("update.feed_redirect_rejected", "更新源发生未授权重定向，已拒绝。");
        }
        if (!response.IsSuccessStatusCode)
        {
            return Result<byte[]>.Fail(
                response.StatusCode == HttpStatusCode.NotFound ? "update.feed_not_found" : "update.feed_failed",
                response.StatusCode == HttpStatusCode.NotFound ? "更新源没有该通道的清单。" : "读取更新清单失败。",
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return Result<byte[]>.Fail("update.feed_too_large", "更新清单超过大小限制。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return Result<byte[]>.Fail("update.feed_too_large", "更新清单超过大小限制。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Result<byte[]>.Ok(buffer.ToArray());
    }
}
