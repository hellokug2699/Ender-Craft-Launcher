// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Core;
using Ecl.Download;
using Ecl.Java;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaArchiveInstallerTests
{
    [Fact]
    public async Task ExtractsArchiveAndReturnsJavaRuntime()
    {
        using var environment = new TestEnvironment();
        var archiveBytes = CreateArchive();
        var downloadService = new ArchiveDownloadService(archiveBytes);

        await using var services = environment.CreateServices(collection =>
        {
            collection.AddSingleton<IDownloadService>(downloadService);
        });

        var settings = services.GetRequiredService<Ecl.Configuration.Settings.ISettingsService>();
        Assert.True(settings.TrySetRaw("java.runtimeProvider", "adoptium", null, out var error), error);

        var result = await services.GetRequiredService<IJavaInstallService>()
            .DownloadAsync(new JavaRequirement(21));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var runtime = result.Value!;
        Assert.Equal(21, runtime.Version?.Major);
        Assert.True(File.Exists(runtime.ExecutablePath));
        Assert.StartsWith(environment.Paths.RuntimesDirectory, runtime.HomePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatedInstallOfSameComponentUsesDeterministicSuffixAndKeepsFirst()
    {
        using var environment = new TestEnvironment();
        var downloadService = new ArchiveDownloadService(CreateArchive());

        await using var services = environment.CreateServices(collection =>
        {
            collection.AddSingleton<IDownloadService>(downloadService);
        });

        var settings = services.GetRequiredService<Ecl.Configuration.Settings.ISettingsService>();
        Assert.True(settings.TrySetRaw("java.runtimeProvider", "adoptium", null, out var error), error);
        var install = services.GetRequiredService<IJavaInstallService>();

        var first = await install.DownloadAsync(new JavaRequirement(21));
        Assert.True(first.IsSuccess, first.Error?.ToString());

        var second = await install.DownloadAsync(new JavaRequirement(21));
        Assert.True(second.IsSuccess, second.Error?.ToString());

        Assert.NotEqual(first.Value!.HomePath, second.Value!.HomePath);
        Assert.True(Directory.Exists(first.Value.HomePath));
        Assert.EndsWith("-2", Path.GetFileName(second.Value.HomePath), StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(environment.Paths.RuntimesDirectory),
            path => path.EndsWith(".staging", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsJavaHomeInNestedArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-java-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "jdk-21", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);

            var home = JavaArchiveInstaller.FindJavaHome(root);

            Assert.Equal(Path.Combine(root, "jdk-21"), home);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "jdk-21/bin/java.exe", string.Empty);
            WriteEntry(archive, "jdk-21/bin/java", string.Empty);
            WriteEntry(archive, "jdk-21/release", """
                JAVA_VERSION="21.0.1"
                IMPLEMENTOR="Fake"
                OS_ARCH="x86_64"
                """);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class ArchiveDownloadService(byte[] archiveBytes) : IDownloadService
    {
        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(request.DestinationPath, archiveBytes);
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, archiveBytes.Length));
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("""
                [
                  {
                    "binary": {
                      "package": {
                        "checksum": null,
                        "link": "https://example.test/fake-jdk.zip",
                        "name": "fake-jdk.zip",
                        "size": null
                      }
                    },
                    "version": { "semver": "21.0.1" }
                  }
                ]
                """);
    }
}
