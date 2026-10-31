// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Diagnostics;

/// <summary>
/// 导出实例日志、崩溃报告、安装诊断与系统信息，供用户反馈。
/// 不包含账户文件；文本内容统一经过脱敏。
/// </summary>
public sealed partial class DiagnosticPackageService(
    IInstanceService instances,
    IAppPaths paths,
    IJavaLocator javaLocator,
    ILogger<DiagnosticPackageService> logger) : IDiagnosticPackageService
{
    private const int MaxTextBytes = 2 * 1024 * 1024;
    private const int MaxLogFiles = 5;

    public async Task<Result<DiagnosticPackageReport>> ExportAsync(
        string instanceId,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<DiagnosticPackageReport>.Fail("instance.not_found", "实例不存在。");
        }

        var output = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                paths.LogsDirectory,
                "diagnostics",
                $"ecl-diagnostics-{instance.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip")
            : Path.GetFullPath(outputPath);

        try
        {
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }

            var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
            var runtimes = await javaLocator.FindAllAsync(cancellationToken).ConfigureAwait(false);
            var entries = 0;

            using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
            {
                var manifest = new
                {
                    generatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    launcherVersion = typeof(DiagnosticPackageService).Assembly.GetName().Version?.ToString(3),
                    instance = new
                    {
                        instance.Id,
                        instance.Name,
                        instance.GameVersion,
                        instance.VersionId,
                        instance.Loader,
                        instance.LoaderVersion,
                        instance.FabricApiVersion,
                        instance.OptiFineVersion,
                    },
                    environment = new
                    {
                        os = Environment.OSVersion.ToString(),
                        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    },
                    java = runtimes.Select(runtime => new
                    {
                        runtime.HomePath,
                        runtime.ExecutablePath,
                        runtime.Vendor,
                        runtime.Architecture,
                        version = runtime.Version?.Raw,
                    }),
                };

                entries += AddText(
                    archive,
                    "manifest.json",
                    Redact(JsonSerializer.Serialize(manifest, EclJson.Indented)));

                entries += AddFile(archive, "instance.json", paths.GetInstanceMetadataFile(instance.Id));
                entries += AddFile(archive, "settings.json", paths.GetInstanceSettingsFile(instance.Id));
                entries += AddFile(archive, "config/global.json", paths.GlobalSettingsFile);

                foreach (var crash in NewestFiles(Path.Combine(gameDirectory, "crash-reports"), "*.txt", 5))
                {
                    entries += AddFile(archive, $"crash-reports/{Path.GetFileName(crash)}", crash);
                }

                foreach (var log in NewestFiles(Path.Combine(gameDirectory, "logs"), "*.log", MaxLogFiles))
                {
                    entries += AddFile(archive, $"minecraft/logs/{Path.GetFileName(log)}", log);
                }

                foreach (var log in NewestFiles(paths.GetInstanceLogsDirectory(instance.Id), "*.log", MaxLogFiles))
                {
                    entries += AddFile(archive, $"instance-logs/{Path.GetFileName(log)}", log);
                }

                var installDirectory = Path.Combine(paths.LogsDirectory, "install");
                foreach (var diagnostic in NewestFiles(installDirectory, $"install-{instance.Id}-*.json", 5))
                {
                    entries += AddFile(archive, $"install/{Path.GetFileName(diagnostic)}", diagnostic);
                }

                foreach (var log in NewestFiles(paths.LogsDirectory, "ecl-*.log", 3))
                {
                    entries += AddFile(archive, $"launcher-logs/{Path.GetFileName(log)}", log);
                }
            }

            var size = new FileInfo(output).Length;
            logger.LogInformation("已导出诊断包 {Path}（{Entries} 个文件，{Bytes} 字节）。", output, entries, size);
            return Result<DiagnosticPackageReport>.Ok(new DiagnosticPackageReport(output, entries, size));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "导出诊断包失败：{Path}", output);
            return Result<DiagnosticPackageReport>.Fail(
                "diagnostics.export_failed",
                $"导出诊断包失败：{exception.Message}");
        }
    }

    private int AddFile(ZipArchive archive, string entryName, string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            var bytes = ReadCapped(path);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var stream = entry.Open();
            var text = Redact(Encoding.UTF8.GetString(bytes));
            var output = Encoding.UTF8.GetBytes(text);
            stream.Write(output);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "跳过无法读取的诊断文件：{Path}", path);
            return 0;
        }
    }

    private static int AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        return 1;
    }

    private static byte[] ReadCapped(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, MaxTextBytes);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = stream.Read(buffer, read, length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static IEnumerable<string> NewestFiles(string directory, string pattern, int count)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(count)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = BearerPattern().Replace(text, "Bearer ***");
        result = TokenPattern().Replace(result, "$1***");
        result = SecretRedaction.CommandLine(result);
        // 第三方（Yggdrasil）token 不是 JWT：客户端连接 Realms 失败时会把它原样写进日志。
        result = SignedJwtPattern().Replace(result, "$1***");
        return UrlCredentialsPattern().Replace(result, "$1***@");
    }

    [GeneratedRegex(
        "(?i)(\"?(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|token)\"?\\s*[:=]\\s*\"?)[^\"\\s,}]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(SignedJWT:\s*)[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex SignedJwtPattern();

    [GeneratedRegex("(://)[^/@\\s]+@")]
    private static partial Regex UrlCredentialsPattern();
}
