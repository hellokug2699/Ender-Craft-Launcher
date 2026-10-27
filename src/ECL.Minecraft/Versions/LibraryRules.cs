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
        VersionRuleEvaluator.IsAllowed(library.Rules, osName, osArch, features);

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
