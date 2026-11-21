// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.Minecraft.Versions;

public static class LibraryRules
{
    public static string CurrentOsName => OperatingSystem.IsWindows()
        ? "windows"
        : OperatingSystem.IsMacOS()
            ? "osx"
            : "linux";

    public static string CurrentArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        _ => "unknown",
    };

    public static bool IsAllowed(
        VersionLibrary library,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features = null) =>
        VersionRuleEvaluator.IsAllowed(library.Rules, osName, osArch, features) &&
        !IsForeignNativeArtifact(library, osName, osArch);

    /// <summary>
    /// 剔除与当前平台不符的原生 jar。部分加载器产物把平台写进坐标而不写 <c>rules</c>：
    /// 既可能是 classifier（<c>io.netty:netty-transport-native-epoll:4.2.15.Final:linux-x86_64</c>），
    /// 也可能被直接拼进版本段（<c>…:4.2.15.Final-linux-x86_64</c>，实测整合包如此）。
    /// 这类 jar 对当前 JVM 无用；Windows 上带着 Linux 原生库只会污染类路径与文件校验。
    /// 无法识别的平台写法保持原行为（允许）。
    /// </summary>
    public static bool IsForeignNativeArtifact(VersionLibrary library, string osName, string osArch)
    {
        if (IsModernNativeClassifier(library))
        {
            // natives-xxx / native-xxx 由 NativeClassifierPriority 按平台与架构挑选，不在这里判断。
            return false;
        }

        var (artifactOs, artifactArch) = GetNativePlatform(library);
        if (artifactOs is null)
        {
            return false;
        }

        var currentOs = NormalizeOsToken(osName) ?? osName.Trim().ToLowerInvariant();
        if (!string.Equals(artifactOs, currentOs, StringComparison.Ordinal))
        {
            return true;
        }

        if (artifactArch is null)
        {
            return false;
        }

        var currentArch = VersionRuleEvaluator.NormalizeArch(osArch);
        if (string.IsNullOrWhiteSpace(currentArch) ||
            string.Equals(currentArch, "unknown", StringComparison.Ordinal))
        {
            // 架构未知时不按架构剔除，避免歧义机器上删掉唯一可用的原生库。
            return false;
        }

        return !string.Equals(artifactArch, currentArch, StringComparison.Ordinal);
    }

    /// <summary>从 classifier 或版本段尾部解析原生库的平台/架构；解析不出时返回 (null, null)。</summary>
    private static (string? Os, string? Arch) GetNativePlatform(VersionLibrary library)
    {
        var classifier = MavenCoordinates.GetClassifier(library.Name);
        if (classifier is not null && TryParsePlatformToken(classifier, out var classifierOs, out var classifierArch))
        {
            return (classifierOs, classifierArch);
        }

        var version = MavenCoordinates.GetVersion(library.Name);
        if (version is not null && TryParsePlatformPrefix(version, out var versionOs, out var versionArch))
        {
            return (versionOs, versionArch);
        }

        return (null, null);
    }

    private static bool TryParsePlatformToken(string token, out string? os, out string? arch)
    {
        var parts = token.Trim().ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        os = parts.Length > 0 ? NormalizeOsToken(parts[0]) : null;
        arch = parts.Length > 1 ? NormalizeArchToken(parts[1]) : null;
        return os is not null;
    }

    /// <summary>匹配以 <c>-&lt;os&gt;[-&lt;arch&gt;]</c> 结尾的版本段（classifier 被拼进 version 的写法）。</summary>
    private static bool TryParsePlatformPrefix(string version, out string? os, out string? arch)
    {
        os = null;
        arch = null;

        var normalized = version.Trim().ToLowerInvariant();
        var separator = normalized.LastIndexOf('-');
        if (separator < 0 || separator == normalized.Length - 1)
        {
            return false;
        }

        var last = normalized[(separator + 1)..];
        var lastArch = NormalizeArchToken(last);
        var head = normalized[..separator];

        if (lastArch is not null)
        {
            var secondSeparator = head.LastIndexOf('-');
            if (secondSeparator < 0)
            {
                return false;
            }

            var headOs = NormalizeOsToken(head[(secondSeparator + 1)..]);
            if (headOs is null)
            {
                return false;
            }

            os = headOs;
            arch = lastArch;
            return true;
        }

        var directOs = NormalizeOsToken(last);
        if (directOs is null)
        {
            return false;
        }

        os = directOs;
        return true;
    }

    private static string? NormalizeOsToken(string token) => token.Trim().ToLowerInvariant() switch
    {
        "windows" or "win" or "win32" or "win64" => "windows",
        "osx" or "macos" or "macosx" or "mac" or "darwin" => "osx",
        "linux" => "linux",
        _ => null,
    };

    private static string? NormalizeArchToken(string token) => token.Trim().ToLowerInvariant() switch
    {
        "x86_64" or "x86-64" or "x64" or "amd64" => "x86_64",
        "x86" or "i386" or "i686" or "32" => "x86",
        "aarch_64" or "aarch64" or "arm64" or "arm_64" => "arm64",
        "arm32" or "arm" or "armv7" => "arm32",
        "ppcle_64" or "ppc64le" => "ppc64le",
        "s390_64" => "s390x",
        "riscv64" => "riscv64",
        _ => null,
    };

    public static bool IsModernNativeClassifier(VersionLibrary library)
    {
        var classifier = MavenCoordinates.GetClassifier(library.Name);
        return classifier is not null &&
               (classifier.StartsWith("natives", StringComparison.OrdinalIgnoreCase) ||
                classifier.StartsWith("native-", StringComparison.OrdinalIgnoreCase));
    }

    public static VersionLibraryArtifact? GetPrimaryArtifact(VersionLibrary library)
    {
        if (library.Downloads?.Artifact is null &&
            (library.Natives is { Count: > 0 } || library.Downloads?.Classifiers is { Count: > 0 }))
        {
            return null;
        }

        var artifact = library.Downloads?.Artifact;
        var path = artifact?.Path ?? MavenCoordinates.GetPath(library.Name);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var url = artifact?.Url;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(library.Url))
        {
            url = ResolveArtifactUrl(library.Url, path);
        }

        return new VersionLibraryArtifact
        {
            Path = path,
            Url = url,
            Sha1 = artifact?.Sha1 ?? library.Sha1,
            Size = artifact?.Size ?? library.Size,
        };
    }

    /// <summary>
    /// 旧版安装器既可能给出 Maven 根地址，也可能直接给出完整文件 URL。
    /// 完整文件 URL 不应再拼接一次 Maven 相对路径。
    /// </summary>
    public static string ResolveArtifactUrl(string? url, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL 不能为空。", nameof(url));
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return CombineUrl(trimmed, relativePath);
        }

        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var uriPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (uriPath.EndsWith('/'))
        {
            return CombineUrl(trimmed, normalizedPath);
        }

        if (uriPath.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 完整文件 URL 可能带 classifier（例如 -universal.jar），与按 name 推导的路径不完全相同。
        var extension = Path.GetExtension(uriPath);
        if (extension is ".jar" or ".zip" or ".lzma" or ".txt" or ".json")
        {
            return trimmed;
        }

        var fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        if (fileName.Length > 0 &&
            uriPath.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return CombineUrl(trimmed, normalizedPath);
    }

    public static VersionLibraryArtifact? GetNativeArtifact(VersionLibrary library, string osName, string osArch)
    {
        if (IsModernNativeClassifier(library))
        {
            return GetPrimaryArtifact(library);
        }

        if (library.Natives is not null)
        {
            var classifier = library.Natives
                .FirstOrDefault(pair => VersionRuleEvaluator.OsKeyMatches(pair.Key, osName))
                .Value;

            if (string.IsNullOrWhiteSpace(classifier))
            {
                return null;
            }

            classifier = classifier.Replace("${arch}", ArchBits(osArch), StringComparison.OrdinalIgnoreCase);
            return GetClassifierArtifact(library, classifier);
        }

        var nativeClassifiers = library.Downloads?.Classifiers;
        if (nativeClassifiers is null)
        {
            return null;
        }

        var descriptor = nativeClassifiers.Keys
            .Select(key => (Key: key, Priority: NativeClassifierPriority(key, osName, osArch)))
            .Where(item => item.Priority > 0)
            .OrderByDescending(item => item.Priority)
            .Select(item => item.Key)
            .FirstOrDefault();

        return descriptor is null ? null : GetClassifierArtifact(library, descriptor);
    }

    public static int NativeClassifierPriority(string classifier, string osName, string osArch)
    {
        var normalized = classifier.ToLowerInvariant();
        var prefixLength = normalized.StartsWith("natives-", StringComparison.Ordinal)
            ? "natives-".Length
            : normalized.StartsWith("native-", StringComparison.Ordinal)
                ? "native-".Length
                : -1;

        if (prefixLength < 0)
        {
            return -1;
        }

        var suffix = normalized[prefixLength..];
        var osPart = suffix.Split('-')[0];
        if (!VersionRuleEvaluator.OsKeyMatches(osPart, osName))
        {
            return -1;
        }

        var archPart = suffix.Length > osPart.Length ? suffix[(osPart.Length + 1)..] : string.Empty;
        var normalizedArch = osArch switch
        {
            "x86_64" => "x64",
            "x86" => "x86",
            "arm64" => "arm64",
            "arm32" => "arm32",
            _ => osArch,
        };

        return (normalizedArch, archPart) switch
        {
            ("x64", "") or ("x64", "64") or ("x64", "x64") or ("x64", "x86_64") => 10,
            ("x86", "x86") or ("x86", "32") => 10,
            ("x86", "") => 5,
            ("arm64", "arm64") => 10,
            ("arm32", "arm32") => 10,
            _ => -1,
        };
    }

    private static VersionLibraryArtifact? GetClassifierArtifact(VersionLibrary library, string classifier)
    {
        if (library.Downloads?.Classifiers is not null &&
            library.Downloads.Classifiers.TryGetValue(classifier, out var artifact))
        {
            var artifactPath = artifact.Path ?? MavenCoordinates.GetPath($"{library.Name}:{classifier}");
            var artifactUrl = artifact.Url;
            if (string.IsNullOrWhiteSpace(artifactUrl) && !string.IsNullOrWhiteSpace(library.Url) && artifactPath is not null)
            {
                artifactUrl = ResolveArtifactUrl(library.Url, artifactPath);
            }

            return new VersionLibraryArtifact
            {
                Path = artifactPath,
                Url = artifactUrl,
                Sha1 = artifact.Sha1,
                Size = artifact.Size,
            };
        }

        var path = MavenCoordinates.GetPath($"{library.Name}:{classifier}");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new VersionLibraryArtifact
        {
            Path = path,
            Url = string.IsNullOrWhiteSpace(library.Url) ? null : ResolveArtifactUrl(library.Url, path),
            Sha1 = library.Sha1,
            Size = library.Size,
        };
    }

    private static string ArchBits(string osArch) => osArch switch
    {
        "x86_64" or "arm64" => "64",
        "x86" or "arm32" => "32",
        _ => "64",
    };

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalizedBase), relativePath.Replace('\\', '/')).ToString();
    }
}
