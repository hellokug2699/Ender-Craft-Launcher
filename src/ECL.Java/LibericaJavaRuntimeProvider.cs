// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Download;

namespace Ecl.Java;

public sealed class LibericaJavaRuntimeProvider(IDownloadService downloads) : IJavaRuntimeProvider
{
    public string Id => "liberica";

    public string DisplayName => "BellSoft Liberica";

    public async Task<Result<JavaRuntimePackage>> ResolveAsync(
        JavaRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        if (!JavaProviderPlatform.TryGet(out var platform))
        {
            return Result<JavaRuntimePackage>.Fail(
                "java.platform_unsupported",
                $"Liberica 不支持当前平台：{JavaPlatform.DescribeCurrentPlatform()}");
        }

        var arch = platform.Arch == "arm64" ? "arm" : "x86";
        var packageType = platform.ArchiveKind == JavaArchiveKind.Zip ? "zip" : "tar.gz";
        var url =
            "https://api.bell-sw.com/v1/liberica/releases" +
            "?version-modifier=latest" +
            $"&version-feature={requirement.MinimumMajor}" +
            $"&bitness={platform.Bitness}" +
            $"&os={Uri.EscapeDataString(platform.Os)}" +
            $"&arch={arch}" +
            $"&package-type={Uri.EscapeDataString(packageType)}" +
            "&bundle-type=jdk";

        string json;
        try
        {
            json = await downloads
                .DownloadTextAsync([new Uri(url)], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DownloadException or HttpRequestException or IOException)
        {
            return Result<JavaRuntimePackage>.Fail("java.provider_failed", $"Liberica 查询失败：{exception.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return Result<JavaRuntimePackage>.Fail(
                    "java.provider_failed",
                    $"Liberica 没有 Java {requirement.MinimumMajor} 的 {platform.Os}-{platform.Arch} 构建。");
            }

            var release = document.RootElement
                .EnumerateArray()
                .FirstOrDefault(item =>
                    !item.TryGetProperty("latestInFeatureVersion", out var latest) ||
                    latest.ValueKind != JsonValueKind.False);
            if (release.ValueKind == JsonValueKind.Undefined)
            {
                release = document.RootElement[0];
            }

            var link = release.TryGetProperty("downloadUrl", out var linkElement)
                ? linkElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(link))
            {
                return Result<JavaRuntimePackage>.Fail("java.provider_failed", "Liberica 返回的下载信息无效。");
            }

            var fileName = release.TryGetProperty("filename", out var nameElement)
                ? nameElement.GetString()
                : null;
            fileName ??= Path.GetFileName(new Uri(link).AbsolutePath);
            var versionText = release.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
            var sha1 = release.TryGetProperty("sha1", out var shaElement) &&
                       shaElement.ValueKind == JsonValueKind.String
                ? shaElement.GetString()
                : null;
            long? size = release.TryGetProperty("size", out var sizeElement) &&
                         sizeElement.ValueKind == JsonValueKind.Number &&
                         sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : null;

            return Result<JavaRuntimePackage>.Ok(new JavaRuntimePackage(
                Id,
                requirement.Component ?? $"java-{requirement.MinimumMajor}",
                $"Liberica {versionText ?? $"Java {requirement.MinimumMajor}"}",
                string.IsNullOrWhiteSpace(fileName) ? $"liberica-{requirement.MinimumMajor}.zip" : fileName,
                link,
                Sha1: sha1,
                Sha256: null,
                Size: size,
                Version: JavaVersion.TryParse(versionText),
                ArchiveKind: fileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true
                    ? JavaArchiveKind.Zip
                    : JavaArchiveKind.TarGz));
        }
        catch (JsonException exception)
        {
            return Result<JavaRuntimePackage>.Fail("java.provider_failed", $"Liberica 返回的 JSON 无效：{exception.Message}");
        }
    }
}
