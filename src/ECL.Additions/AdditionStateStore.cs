// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.Additions;

public sealed record AdditionStateLoad(InstalledAdditionsDocument Document, bool IsCorrupt, string? Error);

/// <summary>
/// 已安装扩展的本地状态。状态文件损坏时返回空状态并要求重新安装，而不是猜测或静默修补；
/// 写入采用临时文件加原子替换，避免中断后留下半写状态（ADR 0032 §7-8）。
/// </summary>
public sealed class AdditionStateStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new ReleaseVersionJsonConverter(),
            new AdditionIdJsonConverter(),
        },
    };

    public AdditionStateLoad Load()
    {
        if (!File.Exists(Path))
        {
            return new AdditionStateLoad(new InstalledAdditionsDocument(), IsCorrupt: false, null);
        }

        try
        {
            var document = JsonSerializer.Deserialize<InstalledAdditionsDocument>(
                File.ReadAllText(Path),
                JsonOptions);
            if (document is null || document.SchemaVersion > InstalledAdditionsDocument.CurrentSchemaVersion)
            {
                return new AdditionStateLoad(
                    new InstalledAdditionsDocument(),
                    IsCorrupt: true,
                    "扩展状态文件版本不受支持。");
            }

            document.Additions ??= [];
            return new AdditionStateLoad(document, IsCorrupt: false, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AdditionStateLoad(
                new InstalledAdditionsDocument(),
                IsCorrupt: true,
                $"扩展状态文件损坏：{exception.Message}");
        }
    }

    public Result Save(InstalledAdditionsDocument document)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(document, JsonOptions),
                new System.Text.UTF8Encoding(false));
            File.Move(temporary, Path, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("additions.state_write_failed", "写入扩展状态失败。", exception.Message);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
