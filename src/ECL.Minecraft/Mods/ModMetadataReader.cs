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
                return ReadFabric(fabric, jarPath);
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

    private static ModMetadataReadResult ReadFabric(ZipArchiveEntry entry, string jarPath)
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

        return new ModMetadataReadResult(
            new ModMetadata(
                id.Trim(),
                GetString(root, "name") ?? Path.GetFileNameWithoutExtension(jarPath),
                GetString(root, "version") ?? "unknown",
                GetString(root, "description"),
                ModPlatform.Fabric,
                dependencies,
                breaks),
            null);
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
