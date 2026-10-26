// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ecl.Download;
using Ecl.Java;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaInstallServiceTests
{
    [Fact]
    public async Task DownloadsRuntimeAndReturnsLocatorResult()
    {
        using var environment = new TestEnvironment();
        var platformKey = JavaPlatform.GetIndexPlatformKey();
        var manifestUrl = "https://piston-meta.mojang.com/v1/packages/abc/manifest.json";
        var javaExecutableName = OperatingSystem.IsWindows() ? "java.exe" : "java";

        var indexJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [platformKey] = new Dictionary<string, object>
            {
                ["java-runtime-gamma"] = new[]
                {
                    new
                    {
                        manifest = new { url = manifestUrl, sha1 = new string('0', 40), size = 1 },
                        version = new { name = "17.0.15" },
                    },
                },
            },
        });

        var manifestJson = JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, object>
            {
                ["bin"] = new { type = "directory" },
                [$"bin/{javaExecutableName}"] = new
                {
                    type = "file",
                    executable = true,
                    downloads = new
                    {
                        raw = new
                        {
                            url = "https://piston-data.mojang.com/v1/objects/abc/java",
                            sha1 = new string('0', 40),
                            size = 1,
                        },
                    },
                },
                ["release"] = new
                {
                    type = "file",
                    downloads = new
                    {
                        raw = new
                        {
                            url = "https://piston-data.mojang.com/v1/objects/def/release",
                            sha1 = new string('0', 40),
                            size = 1,
                        },
                    },
                },
            },
        });

        var download = new FakeDownloadService(indexJson, manifestUrl, manifestJson, javaExecutableName);
        var locator = new FakeJavaLocator(javaExecutableName);

        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(download);
            services.AddSingleton<IJavaLocator>(locator);
        });

        var stages = new List<JavaInstallStage>();
        var progress = new InlineProgress<JavaInstallProgress>(update => stages.Add(update.Stage));

        var result = await provider.GetRequiredService<IJavaInstallService>()
            .EnsureAsync(new JavaRequirement(17, "java-runtime-gamma"), progress);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var runtime = result.Value!;
        Assert.Equal(17, runtime.Version?.Major);
        Assert.True(File.Exists(runtime.ExecutablePath));
        Assert.Contains(JavaInstallStage.Files, stages);
        Assert.Contains(JavaInstallStage.Completed, stages);
        Assert.NotEmpty(download.Requests);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class FakeDownloadService(
        string indexJson,
        string manifestUrl,
        string manifestJson,
        string javaExecutableName) : IDownloadService
    {
        public ConcurrentBag<DownloadRequest> Requests { get; } = [];

        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                Requests.Add(request);
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var fileName = Path.GetFileName(request.DestinationPath);
                var content = fileName == "release"
                    ? "JAVA_VERSION=\"17.0.15\"\nOS_ARCH=\"x86_64\"\n"
                    : fileName == javaExecutableName
                        ? string.Empty
                        : "x";

                File.WriteAllText(request.DestinationPath, content);
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count));
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default)
        {
            var url = urls.FirstOrDefault()?.ToString() ?? string.Empty;
            if (url.Contains("all.json", StringComparison.Ordinal))
            {
                return Task.FromResult(indexJson);
            }

            if (url == manifestUrl)
            {
                return Task.FromResult(manifestJson);
            }

            return Task.FromResult("{}");
        }
    }

    private sealed class FakeJavaLocator(string javaExecutableName) : IJavaLocator
    {
        public Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaRuntime>>([]);

        public JavaRuntime? FromPath(string path)
        {
            var executable = Path.Combine(path, "bin", javaExecutableName);
            if (!File.Exists(executable))
            {
                return null;
            }

            return new JavaRuntime(
                path,
                executable,
                "Test",
                "x86_64",
                JavaVersion.TryParse("17.0.15"));
        }
    }
}
