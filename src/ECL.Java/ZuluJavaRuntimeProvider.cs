// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Download;

namespace Ecl.Java;

public sealed class ZuluJavaRuntimeProvider(IDownloadService downloads) : IJavaRuntimeProvider
{
    public string Id => "zulu";

    public string DisplayName => "Azul Zulu";

    public async Task<Result<JavaRuntimePackage>> ResolveAsync(
        JavaRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        if (!JavaProviderPlatform.TryGet(out var platform))
        {
            return Result<JavaRuntimePackage>.Fail(
                "java.platform_unsupported",
                $"Zulu 不支持当前平台：{JavaPlatform.DescribeCurrentPlatform()}");
        }

        var arch = platform.Arch == "arm64" ? "aarch64" : platform.Arch;
        var archiveType = platform.ArchiveKind == JavaArchiveKind.Zip ? "zip" : "tar.gz";
        var url =
            "https://api.azul.com/metadata/v1/zulu/packages/" +
            $"?java_version={requirement.MinimumMajor}" +
            $"&os={Uri.EscapeDataString(platform.Os)}" +
            $"&arch={Uri.EscapeDataString(arch)}" +
            $"&archive_type={Uri.EscapeDataString(archiveType)}" +
            "&java_package_type=jdk&latest=true&release_status=ga&availability_types=CA&page_size=1";

        string json;
        try
        {
            json = await downloads
                .DownloadTextAsync([new Uri(url)], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DownloadException or HttpRequestException or IOException)
        {
            return Result<JavaRuntimePackage>.Fail("java.provider_failed", $"Zulu 查询失败：{exception.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return Result<JavaRuntimePackage>.Fail(
                    "java.provider_failed",
                    $"Zulu 没有 Java {requirement.MinimumMajor} 的 {platform.Os}-{platform.Arch} 构建。");
            }

            var release = document.RootElement[0];
            var link = release.TryGetProperty("download_url", out var linkElement)
                ? linkElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(link))
            {
                return Result<JavaRuntimePackage>.Fail("java.provider_failed", "Zulu 返回的下载信息无效。");
            }

            var fileName = release.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            fileName ??= Path.GetFileName(new Uri(link).AbsolutePath);
            var versionText = release.TryGetProperty("java_version", out var versionElement) &&
                              versionElement.ValueKind == JsonValueKind.Array
                ? string.Join('.', versionElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Number)
                    .Select(item => item.GetInt32()))
                : null;

            return Result<JavaRuntimePackage>.Ok(new JavaRuntimePackage(
                Id,
                requirement.Component ?? $"java-{requirement.MinimumMajor}",
                $"Zulu {versionText ?? $"Java {requirement.MinimumMajor}"}",
                string.IsNullOrWhiteSpace(fileName) ? $"zulu-{requirement.MinimumMajor}.zip" : fileName,
                link,
                Sha1: null,
                Sha256: null,
                Size: null,
                Version: JavaVersion.TryParse(versionText),
                ArchiveKind: fileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true
                    ? JavaArchiveKind.Zip
                    : JavaArchiveKind.TarGz));
        }
        catch (JsonException exception)
        {
            return Result<JavaRuntimePackage>.Fail("java.provider_failed", $"Zulu 返回的 JSON 无效：{exception.Message}");
        }
    }
}
