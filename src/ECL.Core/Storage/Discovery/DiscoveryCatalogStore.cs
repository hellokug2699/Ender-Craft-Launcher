// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Core.Json;

namespace Ecl.Core.Storage.Discovery;

/// <summary>
/// 发现目录的原子读写（F-02 §6.1/§6.4）。损坏时保留原文件副本并按“重建目录”处理；
/// schema 高于当前支持时只读返回，不允许覆盖。
/// </summary>
public sealed class DiscoveryCatalogStore
{
    private readonly string _filePath;
    private readonly string _directory;

    public DiscoveryCatalogStore(string dataRoot)
    {
        _directory = Path.Combine(dataRoot, "config", "instance-engine");
        _filePath = Path.Combine(_directory, "discovery-catalog.json");
    }

    public string FilePath => _filePath;

    public DiscoveryCatalogLoadResult Load()
    {
        if (!File.Exists(_filePath))
        {
            return new DiscoveryCatalogLoadResult(DiscoveryCatalog.Empty, null, null, IsUnsupported: false);
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryCatalogLoadResult(
                DiscoveryCatalog.Empty,
                "discovery.catalog_unreadable",
                $"读取发现目录失败：{exception.Message}",
                IsUnsupported: false);
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<DiscoveryCatalog>(json, EclJson.Default);
            if (catalog is null)
            {
                return RecoverFromCorruption(json, "发现目录为空。");
            }

            if (catalog.SchemaVersion > DiscoveryCatalog.CurrentSchemaVersion)
            {
                return new DiscoveryCatalogLoadResult(
                    DiscoveryCatalog.Empty,
                    "discovery.catalog_unsupported",
                    $"发现目录版本 {catalog.SchemaVersion} 高于当前支持的 {DiscoveryCatalog.CurrentSchemaVersion}，已按只读处理。",
                    IsUnsupported: true);
            }

            return new DiscoveryCatalogLoadResult(Normalize(catalog), null, null, IsUnsupported: false);
        }
        catch (JsonException exception)
        {
            return RecoverFromCorruption(json, $"发现目录 JSON 无效：{exception.Message}");
        }
    }

    public void Save(DiscoveryCatalog catalog)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(catalog, EclJson.Indented);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _filePath, overwrite: true);
    }

    private DiscoveryCatalogLoadResult RecoverFromCorruption(string originalJson, string message)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var archivePath = Path.Combine(
                _directory,
                $"discovery-catalog.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(archivePath, originalJson, Encoding.UTF8);
            message = $"{message} 已保留损坏副本：{archivePath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            message = $"{message} 损坏副本保存失败：{exception.Message}";
        }

        return new DiscoveryCatalogLoadResult(
            DiscoveryCatalog.Empty with { GeneratedAt = DateTimeOffset.UtcNow },
            "discovery.catalog_corrupt",
            message,
            IsUnsupported: false);
    }

    private static DiscoveryCatalog Normalize(DiscoveryCatalog catalog)
    {
        var roots = catalog.Roots
            .Where(root => !string.IsNullOrWhiteSpace(root.RootId))
            .GroupBy(root => root.RootId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var entries = catalog.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DiscoveryId))
            .GroupBy(entry => entry.DiscoveryId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        return catalog with { Roots = roots, Entries = entries };
    }
}

/// <summary>发现目录加载结果。错误存在时调用方必须展示覆盖问题，不得当作“没有条目”。</summary>
public sealed record DiscoveryCatalogLoadResult(
    DiscoveryCatalog Catalog,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsUnsupported);
