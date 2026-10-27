// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Download;

namespace Ecl.Java;

public sealed class AdoptiumJavaRuntimeProvider(IDownloadService downloads) : IJavaRuntimeProvider
{
    public string Id => "adoptium";

    public string DisplayName => "Eclipse Adoptium";

    public async Task<Result<JavaRuntimePackage>> ResolveAsync(
        JavaRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        if (!JavaProviderPlatform.TryGet(out var platform))
        {
            return Result<JavaRuntimePackage>.Fail(
                "java.platform_unsupported",
                $"Adoptium 不支持当前平台：{JavaPlatform.DescribeCurrentPlatform()}");
        }

        var arch = platform.Arch switch
        {
            "x64" => "x64",
            "x86" => "x86",
            "arm64" => "aarch64",
            _ => platform.Arch,
        };
        var os = platform.Os == "macos" ? "mac" : platform.Os;
        var url =
            $"https://api.adoptium.net/v3/assets/latest/{requirement.MinimumMajor}/hotspot" +
            $"?architecture={Uri.EscapeDataString(arch)}&image_type=jdk&os={Uri.EscapeDataString(os)}&vendor=eclipse";

        string json;
        try
        {
            json = await downloads
                .DownloadTextAsync([new Uri(url)], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DownloadException or HttpRequestException or IOException)
        {
            return Result<JavaRuntimePackage>.Fail(
                "java.provider_failed",
                $"Adoptium 查询失败：{exception.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return Result<JavaRuntimePackage>.Fail(
                    "java.provider_failed",
                    $"Adoptium 没有 Java {requirement.MinimumMajor} 的 {platform.Os}-{platform.Arch} 构建。");
            }

            var release = document.RootElement[0];
            if (!TryGetPackage(release, out var package) ||
                !package.TryGetProperty("link", out var linkElement) ||
                linkElement.GetString() is not { Length: > 0 } link)
            {
                return Result<JavaRuntimePackage>.Fail("java.provider_failed", "Adoptium 返回的下载信息无效。");
            }

            var fileName = package.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            fileName ??= Path.GetFileName(new Uri(link).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"adoptium-{requirement.MinimumMajor}.zip";
            }

            var sha256 = package.TryGetProperty("checksum", out var checksumElement) &&
                         checksumElement.ValueKind == JsonValueKind.String
                ? checksumElement.GetString()
                : null;
            long? size = package.TryGetProperty("size", out var sizeElement) &&
                         sizeElement.ValueKind == JsonValueKind.Number &&
                         sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : null;

            var versionText = release.TryGetProperty("version", out var versionElement) &&
                              versionElement.TryGetProperty("semver", out var semverElement)
                ? semverElement.GetString()
                : null;

            return Result<JavaRuntimePackage>.Ok(new JavaRuntimePackage(
                Id,
                requirement.Component ?? $"java-{requirement.MinimumMajor}",
                $"Adoptium {versionText ?? $"Java {requirement.MinimumMajor}"}",
                fileName,
                link,
                Sha1: null,
                Sha256: sha256,
                Size: size,
                Version: JavaVersion.TryParse(versionText),
                ArchiveKind: fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? JavaArchiveKind.Zip
                    : JavaArchiveKind.TarGz));
        }
        catch (JsonException exception)
        {
            return Result<JavaRuntimePackage>.Fail("java.provider_failed", $"Adoptium 返回的 JSON 无效：{exception.Message}");
        }
    }

    private static bool TryGetPackage(JsonElement release, out JsonElement package)
    {
        if (release.TryGetProperty("package", out package))
        {
            return true;
        }

        if (release.TryGetProperty("binary", out var binary) &&
            binary.TryGetProperty("package", out package))
        {
            return true;
        }

        package = default;
        return false;
    }
}
