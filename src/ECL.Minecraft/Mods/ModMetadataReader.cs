// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core.Json;

namespace Ecl.Minecraft.Mods;

public static class ModMetadataReader
{
    private static readonly string[] FabricMetadata = ["fabric.mod.json"];
    private static readonly string[] NeoForgeMetadata = ["META-INF/neoforge.mods.toml"];
    private static readonly string[] ForgeMetadata = ["META-INF/mods.toml", "mods.toml"];
    private static readonly string[] LegacyMetadata = ["mcmod.info"];

    public static ModMetadataReadResult Read(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var manifestVersion = ReadManifestVersion(archive);

            var fabric = FindEntry(archive, FabricMetadata);
            if (fabric is not null)
            {
                return ReadFabric(archive, fabric, jarPath);
            }

            var neoForge = FindEntry(archive, NeoForgeMetadata);
            if (neoForge is not null)
            {
                return ReadForge(neoForge, ModPlatform.NeoForge, manifestVersion);
            }

            var forge = FindEntry(archive, ForgeMetadata);
            if (forge is not null)
            {
                return ReadForge(forge, ModPlatform.Forge, manifestVersion);
            }

            var legacy = FindEntry(archive, LegacyMetadata);
            if (legacy is not null)
            {
                return ReadLegacy(legacy);
            }

            return new ModMetadataReadResult(null, "未找到 fabric.mod.json、mods.toml 或 mcmod.info。");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or UnauthorizedAccessException or JsonException)
        {
            return new ModMetadataReadResult(null, $"读取 Mod 元数据失败：{exception.Message}");
        }
    }

    private static ModMetadataReadResult ReadFabric(ZipArchive archive, ZipArchiveEntry entry, string jarPath)
    {
        using var document = JsonDocument.Parse(ReadText(entry));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ModMetadataReadResult(null, "fabric.mod.json 不是对象。");
        }

        var id = GetString(root, "id") ?? GetString(root, "modid");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ModMetadataReadResult(null, "fabric.mod.json 缺少 id。");
        }

        var dependencies = new List<ModDependency>();
        if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in depends.EnumerateObject())
            {
                dependencies.Add(new ModDependency(
                    property.Name,
                    Mandatory: true,
                    VersionRange: ReadVersionConstraint(property.Value)));
            }
        }

        var breaks = ReadStringList(root, "breaks")
            .Concat(ReadStringList(root, "conflicts"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var provides = ReadStringList(root, "provides")
            .Concat(ReadNestedFabricProvides(archive, root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ModMetadataReadResult(
            new ModMetadata(
                id.Trim(),
                GetString(root, "name") ?? Path.GetFileNameWithoutExtension(jarPath),
                GetString(root, "version") ?? "unknown",
                GetString(root, "description"),
                ModPlatform.Fabric,
                dependencies,
                breaks,
                provides),
            null);
    }

    /// <summary>
    /// Fabric API 等聚合 Mod 把实际模块放在 META-INF/jars/ 下的嵌套 JAR 中；
    /// 顶层 fabric.mod.json 的 provides 只列出 "fabric"。依赖检查需要读取嵌套模块 ID，
    /// 否则 Sodium 等 Mod 会被误报缺少 fabric-rendering-* 模块。
    /// </summary>
    private static IEnumerable<string> ReadNestedFabricProvides(ZipArchive archive, JsonElement root)
    {
        if (!root.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var provided = new List<string>();
        var count = 0;
        foreach (var item in jars.EnumerateArray())
        {
            if (++count > 256 ||
                item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("file", out var file) ||
                file.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var nestedPath = file.GetString();
            if (string.IsNullOrWhiteSpace(nestedPath) || !IsSafeRelativePath(nestedPath, out var safePath))
            {
                continue;
            }

            // Fabric API 的 jars[].file 通常已经包含 META-INF/jars/ 前缀，旧格式则可能只有文件名。
            var nestedArchivePath = safePath.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase)
                ? safePath
                : "META-INF/jars/" + safePath;
            var nestedEntry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.FullName.Replace('\\', '/'),
                    nestedArchivePath,
                    StringComparison.OrdinalIgnoreCase));
            if (nestedEntry is null || nestedEntry.Length > 16 * 1024 * 1024)
            {
                continue;
            }

            try
            {
                using var nestedStream = nestedEntry.Open();
                using var nestedArchive = new ZipArchive(nestedStream, ZipArchiveMode.Read, leaveOpen: false);
                var metadata = FindEntry(nestedArchive, FabricMetadata);
                if (metadata is null || metadata.Length > 2 * 1024 * 1024)
                {
                    continue;
                }

                using var nestedDocument = JsonDocument.Parse(ReadText(metadata));
                var nestedRoot = nestedDocument.RootElement;
                var id = GetString(nestedRoot, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    provided.Add(id.Trim());
                }

                foreach (var nestedProvide in ReadStringList(nestedRoot, "provides"))
                {
                    provided.Add(nestedProvide);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
            {
                // 单个嵌套模块损坏不阻断整个 Mod 的元数据。
            }
        }

        return provided;
    }

    private static bool IsSafeRelativePath(string path, out string normalized)
    {
        normalized = string.Empty;
        var candidate = path.Trim().Replace('\\', '/');
        if (candidate.Length == 0 ||
            candidate.StartsWith('/') ||
            candidate.Contains(':') ||
            candidate.Contains('\0'))
        {
            return false;
        }

        var parts = candidate.Split('/', StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }

    private static ModMetadataReadResult ReadForge(
        ZipArchiveEntry entry,
        ModPlatform platform,
        string? manifestVersion)
    {
        var tables = TomlLite.Parse(ReadText(entry));
        var modTable = tables.FirstOrDefault(table => string.Equals(table.Name, "mods", StringComparison.OrdinalIgnoreCase));
        if (modTable is null)
        {
            return new ModMetadataReadResult(null, "mods.toml 缺少 [[mods]] 段。");
        }

        modTable.Values.TryGetValue("modId", out var id);
        if (string.IsNullOrWhiteSpace(id))
        {
            modTable.Values.TryGetValue("modid", out id);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return new ModMetadataReadResult(null, "mods.toml 缺少 modId。");
        }

        modTable.Values.TryGetValue("version", out var version);
        if (string.IsNullOrWhiteSpace(version) ||
            version.Contains("${file.jarVersion}", StringComparison.OrdinalIgnoreCase))
        {
            version = manifestVersion ?? version ?? "unknown";
        }

        var dependencies = new List<ModDependency>();
        foreach (var table in tables.Where(table =>
                     table.Name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase)))
        {
            if (!table.Values.TryGetValue("modId", out var dependencyId) ||
                string.IsNullOrWhiteSpace(dependencyId))
            {
                continue;
            }

            var mandatory = !table.Values.TryGetValue("mandatory", out var mandatoryText) ||
                            !string.Equals(mandatoryText, "false", StringComparison.OrdinalIgnoreCase);
            table.Values.TryGetValue("versionRange", out var versionRange);
            dependencies.Add(new ModDependency(dependencyId.Trim(), mandatory, versionRange));
        }

        return new ModMetadataReadResult(
            new ModMetadata(
                id.Trim(),
                modTable.Values.GetValueOrDefault("displayName", id.Trim()),
                version,
                modTable.Values.GetValueOrDefault("description"),
                platform,
                dependencies,
                []),
            null);
    }

    private static ModMetadataReadResult ReadLegacy(ZipArchiveEntry entry)
    {
        using var document = JsonDocument.Parse(ReadText(entry));
        var root = document.RootElement;
        JsonElement mod = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() > 0)
            {
                mod = root[0];
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("modList", out var modList) &&
                modList.ValueKind == JsonValueKind.Array &&
                modList.GetArrayLength() > 0)
            {
                mod = modList[0];
            }
            else
            {
                mod = root;
            }
        }

        if (mod.ValueKind == JsonValueKind.Undefined)
        {
            return new ModMetadataReadResult(null, "mcmod.info 中没有 Mod 条目。");
        }

        var id = GetString(mod, "modid") ?? GetString(mod, "modId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ModMetadataReadResult(null, "mcmod.info 缺少 modid。");
        }

        var dependencies = new List<ModDependency>();
        foreach (var dependency in ReadStringList(mod, "requiredMods")
                     .Concat(ReadStringList(mod, "dependencies")))
        {
            var dependencyId = dependency
                .Split(['@', ':'], 2, StringSplitOptions.TrimEntries)[0];
            if (dependencyId.Length > 0)
            {
                dependencies.Add(new ModDependency(dependencyId, Mandatory: true));
            }
        }

        return new ModMetadataReadResult(
            new ModMetadata(
                id.Trim(),
                GetString(mod, "name") ?? id.Trim(),
                GetString(mod, "version") ?? "unknown",
                GetString(mod, "description"),
                ModPlatform.LegacyForge,
                dependencies,
                ReadStringList(mod, "conflicts")),
            null);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static string? ReadManifestVersion(ZipArchive archive)
    {
        var entry = FindEntry(archive, ["META-INF/MANIFEST.MF"]);
        if (entry is null)
        {
            return null;
        }

        try
        {
            foreach (var line in ReadText(entry).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // 忽略无法读取的 manifest。
        }

        return null;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return [value.GetString()!];
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().Select(property => property.Name).ToArray();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static string? ReadVersionConstraint(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => string.Join(", ", element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())),
        _ => null,
    };
}
