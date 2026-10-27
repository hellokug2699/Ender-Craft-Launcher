// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Mods;

public enum ContentPackKind
{
    ResourcePack,
    ShaderPack,
}

public sealed record ContentPackEntry(
    string FileName,
    string FullPath,
    ContentPackKind Kind,
    string Name,
    string? Description,
    int? PackFormat);

public interface IContentPackService
{
    Task<Result<IReadOnlyList<ContentPackEntry>>> ListAsync(
        string instanceId,
        ContentPackKind kind,
        CancellationToken cancellationToken = default);

    Task<Result<ContentPackEntry>> ImportAsync(
        string instanceId,
        ContentPackKind kind,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        CancellationToken cancellationToken = default);
}

public sealed class ContentPackService(
    IInstanceService instances,
    IAppPaths paths) : IContentPackService
{
    public async Task<Result<IReadOnlyList<ContentPackEntry>>> ListAsync(
        string instanceId,
        ContentPackKind kind,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<IReadOnlyList<ContentPackEntry>>.Fail("instance.not_found", "实例不存在。");
        }

        var directory = GetDirectory(instance, kind);
        if (!Directory.Exists(directory))
        {
            return Result<IReadOnlyList<ContentPackEntry>>.Ok([]);
        }

        var entries = Directory
            .EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => ReadEntry(file, kind))
            .ToArray();

        return Result<IReadOnlyList<ContentPackEntry>>.Ok(entries);
    }

    public async Task<Result<ContentPackEntry>> ImportAsync(
        string instanceId,
        ContentPackKind kind,
        string sourcePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentPackEntry>.Fail("instance.not_found", "实例不存在。");
        }

        if (!File.Exists(sourcePath))
        {
            return Result<ContentPackEntry>.Fail("pack.not_found", $"文件不存在：{sourcePath}");
        }

        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Result<ContentPackEntry>.Fail("pack.invalid_file", "只支持 .zip 格式的资源包/光影包。");
        }

        var directory = GetDirectory(instance, kind);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(sourcePath));
        if (File.Exists(destination) && !overwrite)
        {
            return Result<ContentPackEntry>.Fail("pack.target_exists", $"目标已存在：{Path.GetFileName(sourcePath)}");
        }

        try
        {
            File.Copy(sourcePath, destination, overwrite);
            return Result<ContentPackEntry>.Ok(ReadEntry(destination, kind));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<ContentPackEntry>.Fail("pack.import_failed", $"导入失败：{exception.Message}");
        }
    }

    public async Task<Result> DeleteAsync(
        string instanceId,
        ContentPackKind kind,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return Result.Fail("pack.invalid_file", "文件名不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。");
        }

        var path = Path.Combine(GetDirectory(instance, kind), fileName);
        if (!File.Exists(path))
        {
            return Result.Fail("pack.not_found", $"文件不存在：{fileName}");
        }

        try
        {
            File.Delete(path);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("pack.delete_failed", $"删除失败：{exception.Message}");
        }
    }

    private static ContentPackEntry ReadEntry(string path, ContentPackKind kind)
    {
        var fileName = Path.GetFileName(path);
        string name = Path.GetFileNameWithoutExtension(fileName);
        string? description = null;
        int? packFormat = null;

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, "pack.mcmeta", StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                using var stream = entry.Open();
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("pack", out var pack))
                {
                    if (pack.TryGetProperty("pack_format", out var format) &&
                        format.ValueKind == JsonValueKind.Number &&
                        format.TryGetInt32(out var parsedFormat))
                    {
                        packFormat = parsedFormat;
                    }

                    description = ReadDescription(pack);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or JsonException or UnauthorizedAccessException)
        {
            // 无效压缩包仍作为文件列出，导入时可以重新覆盖。
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            name = description;
        }

        return new ContentPackEntry(fileName, Path.GetFullPath(path), kind, name, description, packFormat);
    }

    private static string? ReadDescription(JsonElement pack)
    {
        if (!pack.TryGetProperty("description", out var description))
        {
            return null;
        }

        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object when description.TryGetProperty("text", out var text) &&
                                      text.ValueKind == JsonValueKind.String => text.GetString(),
            _ => description.GetRawText(),
        };
    }

    private string GetDirectory(GameInstance instance, ContentPackKind kind)
    {
        var name = kind == ContentPackKind.ShaderPack ? "shaderpacks" : "resourcepacks";
        return Path.Combine(paths.GetInstanceGameDirectory(instance.Id), name);
    }
}
