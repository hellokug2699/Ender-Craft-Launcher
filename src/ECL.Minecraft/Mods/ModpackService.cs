// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Mods;

public sealed class ModpackService(
    IInstanceService instances,
    IAppPaths paths,
    IDownloadService downloads,
    ILogger<ModpackService> logger) : IModpackService
{
    private static readonly string[] OverrideRoots = ["overrides/", "client-overrides/"];

    public async Task<Result<ModpackImportReport>> ImportAsync(
        string instanceId,
        string archivePath,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModpackImportReport>.Fail("instance.not_found", "实例不存在。");
        }

        if (!File.Exists(archivePath))
        {
            return Result<ModpackImportReport>.Fail("modpack.not_found", $"整合包不存在：{archivePath}");
        }

        var gameDirectory = paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(gameDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var modrinthEntry = FindEntry(archive, "modrinth.index.json");
            var report = modrinthEntry is not null
                ? await ImportModrinthAsync(archive, modrinthEntry, instance, gameDirectory, archivePath, progress, cancellationToken)
                    .ConfigureAwait(false)
                : await ImportCurseForgeAsync(archive, instance, gameDirectory, archivePath, progress, cancellationToken)
                    .ConfigureAwait(false);

            return report;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "导入整合包失败：{Path}", archivePath);
            return Result<ModpackImportReport>.Fail(
                "modpack.import_failed",
                $"导入整合包失败：{exception.Message}");
        }
    }

    public async Task<Result<ModpackExportReport>> ExportAsync(
        string instanceId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModpackExportReport>.Fail("instance.not_found", "实例不存在。");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Result<ModpackExportReport>.Fail("modpack.invalid_output", "导出路径不能为空。");
        }

        try
        {
            var gameDirectory = paths.GetInstanceGameDirectory(instance.Id);
            var fullOutput = Path.GetFullPath(outputPath);
            var outputDirectory = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minecraft"] = instance.GameVersion,
            };

            switch (instance.Loader.ToLowerInvariant())
            {
                case "fabric" when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    dependencies["fabric-loader"] = instance.LoaderVersion;
                    break;
                case "forge" when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    dependencies["forge"] = instance.LoaderVersion;
                    break;
                case "neoforge" when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    dependencies["neoforge"] = instance.LoaderVersion;
                    break;
                case "cleanroom" when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    dependencies["forge"] = instance.LoaderVersion;
                    dependencies["cleanroom"] = instance.LoaderVersion;
                    break;
            }

            var index = new
            {
                formatVersion = 1,
                game = "minecraft",
                versionId = instance.VersionId,
                name = instance.Name,
                summary = "Exported by Ender Craft Launcher",
                files = Array.Empty<object>(),
                dependencies,
            };

            var count = 0;
            using (var archive = ZipFile.Open(fullOutput, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "modrinth.index.json", JsonSerializer.Serialize(index, EclJson.Indented));
                foreach (var root in new[] { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks" })
                {
                    var directory = Path.Combine(gameDirectory, root);
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
                        WriteEntry(archive, $"overrides/{relative}", File.ReadAllBytes(file));
                        count++;
                    }
                }
            }

            logger.LogInformation("已导出整合包 {Path}（{Count} 个文件）。", fullOutput, count);
            return Result<ModpackExportReport>.Ok(new ModpackExportReport(fullOutput, count, ContainsDownloads: false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or NotSupportedException
                                              or ArgumentException)
        {
            logger.LogError(exception, "导出整合包失败：{Path}", outputPath);
            return Result<ModpackExportReport>.Fail(
                "modpack.export_failed",
                $"导出整合包失败：{exception.Message}");
        }
    }

    private async Task<Result<ModpackImportReport>> ImportModrinthAsync(
        ZipArchive archive,
        ZipArchiveEntry indexEntry,
        GameInstance instance,
        string gameDirectory,
        string archivePath,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(ReadText(indexEntry));
        var root = document.RootElement;
        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        var version = GetString(root, "versionId");

        var dependencies = root.TryGetProperty("dependencies", out var deps) &&
                           deps.ValueKind == JsonValueKind.Object
            ? deps
            : default;

        if (dependencies.ValueKind == JsonValueKind.Object &&
            dependencies.TryGetProperty("minecraft", out var minecraft) &&
            minecraft.ValueKind == JsonValueKind.String &&
            !string.Equals(minecraft.GetString(), instance.GameVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Result<ModpackImportReport>.Fail(
                "modpack.game_version_mismatch",
                $"整合包要求 Minecraft {minecraft.GetString()}，当前实例是 {instance.GameVersion}。");
        }

        var packLoader = DetectPackLoader(dependencies);
        if (packLoader is not null && !LoaderMatches(instance.Loader, packLoader))
        {
            return Result<ModpackImportReport>.Fail(
                "modpack.loader_mismatch",
                $"整合包要求 {packLoader} 加载器，当前实例是 {instance.Loader}。");
        }

        var warnings = new List<string>();
        var requests = new List<DownloadRequest>();
        var skipped = 0;

        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = GetString(file, "path");
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                if (file.TryGetProperty("env", out var env) &&
                    env.ValueKind == JsonValueKind.Object &&
                    env.TryGetProperty("client", out var client) &&
                    client.ValueKind == JsonValueKind.String &&
                    string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var destination = ResolveSafePath(gameDirectory, relativePath);
                if (destination is null)
                {
                    warnings.Add($"跳过非法路径：{relativePath}");
                    skipped++;
                    continue;
                }

                var urls = ReadDownloadUrls(file);
                if (urls.Count == 0)
                {
                    warnings.Add($"文件没有可用下载地址：{relativePath}");
                    skipped++;
                    continue;
                }

                var sha1 = file.TryGetProperty("hashes", out var hashes) &&
                           hashes.ValueKind == JsonValueKind.Object &&
                           hashes.TryGetProperty("sha1", out var sha1Element) &&
                           sha1Element.ValueKind == JsonValueKind.String
                    ? sha1Element.GetString()
                    : null;
                long? size = file.TryGetProperty("fileSize", out var sizeElement) &&
                             sizeElement.ValueKind == JsonValueKind.Number &&
                             sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : null;

                requests.Add(new DownloadRequest(
                    destination,
                    urls,
                    sha1,
                    HashingAlgorithm.Sha1,
                    size,
                    relativePath));
            }
        }

        var downloaded = 0;
        if (requests.Count > 0)
        {
            progress?.Report(new ModpackProgress("download", "下载整合包文件…", 0, requests.Count, null));
            var forwarding = new ForwardingProgress<DownloadProgress>(update =>
                progress?.Report(new ModpackProgress(
                    "download",
                    "下载整合包文件…",
                    update.CompletedFiles,
                    update.TotalFiles,
                    update.CurrentFile)));

            try
            {
                var result = await downloads.DownloadAsync(requests, forwarding, cancellationToken).ConfigureAwait(false);
                downloaded = result.DownloadedFiles;
            }
            catch (DownloadException exception)
            {
                return Result<ModpackImportReport>.Fail(
                    "modpack.download_failed",
                    $"下载整合包文件失败：{exception.Message}",
                    string.Join("; ", exception.Failures.Take(5)));
            }
        }

        progress?.Report(new ModpackProgress("overrides", "解压整合包覆盖文件…", 0, 0, null));
        var overrides = ExtractOverrides(archive, gameDirectory);
        WriteMarker(gameDirectory, name, version, archivePath);

        logger.LogInformation(
            "已导入整合包 {Name} 到实例 {InstanceId}：下载 {Downloaded}，跳过 {Skipped}，覆盖 {Overrides}。",
            name,
            instance.Id,
            downloaded,
            skipped,
            overrides);

        return Result<ModpackImportReport>.Ok(new ModpackImportReport(
            name,
            version,
            downloaded,
            skipped,
            overrides,
            warnings));
    }

    private async Task<Result<ModpackImportReport>> ImportCurseForgeAsync(
        ZipArchive archive,
        GameInstance instance,
        string gameDirectory,
        string archivePath,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var manifestEntry = FindEntry(archive, "manifest.json");
        if (manifestEntry is null)
        {
            return Result<ModpackImportReport>.Fail(
                "modpack.unsupported",
                "不是受支持的整合包：缺少 modrinth.index.json 或 manifest.json。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(ReadText(manifestEntry));
        var root = document.RootElement;
        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        var version = GetString(root, "version");

        if (root.TryGetProperty("minecraft", out var minecraft) &&
            minecraft.ValueKind == JsonValueKind.Object &&
            minecraft.TryGetProperty("version", out var gameVersion) &&
            gameVersion.ValueKind == JsonValueKind.String &&
            !string.Equals(gameVersion.GetString(), instance.GameVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Result<ModpackImportReport>.Fail(
                "modpack.game_version_mismatch",
                $"整合包要求 Minecraft {gameVersion.GetString()}，当前实例是 {instance.GameVersion}。");
        }

        progress?.Report(new ModpackProgress("overrides", "解压 CurseForge 覆盖文件…", 0, 0, null));
        var overrides = ExtractOverrides(archive, gameDirectory);
        WriteMarker(gameDirectory, name, version, archivePath);

        var warnings = new List<string>
        {
            "CurseForge 远程文件下载需要 API key；本次只导入了 overrides 中的本地文件。",
        };

        logger.LogInformation(
            "已导入 CurseForge 整合包 {Name} 的覆盖文件到实例 {InstanceId}（{Overrides} 个）。",
            name,
            instance.Id,
            overrides);

        return Result<ModpackImportReport>.Ok(new ModpackImportReport(
            name,
            version,
            DownloadedFiles: 0,
            SkippedFiles: 0,
            overrides,
            warnings));
    }

    private static int ExtractOverrides(ZipArchive archive, string gameDirectory)
    {
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            var root = OverrideRoots.FirstOrDefault(candidate =>
                normalized.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (root is null || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = normalized[root.Length..];
            var destination = ResolveSafePath(gameDirectory, relative);
            if (destination is null)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        return count;
    }

    private void WriteMarker(string gameDirectory, string name, string? version, string archivePath)
    {
        var marker = new
        {
            name,
            version,
            source = Path.GetFileName(archivePath),
            importedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        File.WriteAllText(
            Path.Combine(gameDirectory, ".ecl-modpack.json"),
            JsonSerializer.Serialize(marker, EclJson.Indented));
    }

    private static IReadOnlyList<Uri> ReadDownloadUrls(JsonElement file)
    {
        if (!file.TryGetProperty("downloads", out var downloads) ||
            downloads.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<Uri>();
        foreach (var item in downloads.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri))
            {
                result.Add(uri);
            }
        }

        return result;
    }

    private static string? DetectPackLoader(JsonElement dependencies)
    {
        if (dependencies.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (dependencies.TryGetProperty("fabric-loader", out _))
        {
            return "fabric";
        }

        if (dependencies.TryGetProperty("neoforge", out _))
        {
            return "neoforge";
        }

        if (dependencies.TryGetProperty("forge", out _))
        {
            return "forge";
        }

        return null;
    }

    private static bool LoaderMatches(string instanceLoader, string packLoader)
    {
        var loader = instanceLoader.ToLowerInvariant();
        return packLoader switch
        {
            "fabric" => loader == "fabric",
            "forge" => loader is "forge" or "cleanroom",
            "neoforge" => loader == "neoforge",
            _ => true,
        };
    }

    private static string? ResolveSafePath(string root, string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
