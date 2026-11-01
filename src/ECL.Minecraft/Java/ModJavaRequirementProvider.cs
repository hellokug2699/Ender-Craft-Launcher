// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Java;

/// <summary>
/// 为一个实例提供额外 Java 要求（Loader/Mod/实例策略）。实现必须隔离单个损坏来源，
/// 不能让一个坏 Mod 或坏元数据阻断整个实例分析。
/// </summary>
public interface IJavaRequirementProvider
{
    Task<IReadOnlyList<JavaRequirementSource>> GetRequirementsAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 从已启用 Mod jar 的 class 文件版本推导 Java 下限。
/// 这是“可证明的下界”证据：class file 65 无法在 Java 21 以下加载；
/// 但它不证明更高 Java 一定完全兼容（native、反射、API 行为仍可能不兼容）。
/// 结果按文件长度/修改时间缓存到数据目录，避免每次启动都重新扫描数百个 jar。
/// </summary>
public sealed class ModJavaRequirementProvider : IJavaRequirementProvider
{
    private const int MaxModFiles = 512;
    private const int MaxClassEntriesPerJar = 20000;
    private const int ClassFileHeaderLength = 8;
    private const int FirstClassMajor = 45;
    private const int MaxParallelScans = 4;

    private readonly IAppPaths _paths;
    private readonly ILogger<ModJavaRequirementProvider> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private bool _cacheLoaded;

    public ModJavaRequirementProvider(IAppPaths paths, ILogger<ModJavaRequirementProvider> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task<IReadOnlyList<JavaRequirementSource>> GetRequirementsAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<JavaRequirementSource>>(
            () => Scan(instance, cancellationToken),
            cancellationToken);

    private IReadOnlyList<JavaRequirementSource> Scan(
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var modsDirectory = Path.Combine(InstancePaths.GetGameDirectory(_paths, instance), "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return [];
        }

        string[] modFiles;
        try
        {
            modFiles = Directory
                .EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxModFiles)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "无法枚举实例 {InstanceId} 的 Mod 目录。", instance.Id);
            return [];
        }

        EnsureCacheLoaded();
        var cacheChanged = 0;
        var majors = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(
            modFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelScans,
                CancellationToken = cancellationToken,
            },
            file =>
            {
                var major = GetJavaMajor(file, cancellationToken, out var updated);
                if (updated)
                {
                    Interlocked.Exchange(ref cacheChanged, 1);
                }

                if (major > 8)
                {
                    majors[file] = major;
                }
            });

        if (cacheChanged != 0)
        {
            SaveCache();
        }

        return modFiles
            .Where(majors.ContainsKey)
            .Select(file => new JavaRequirementSource(
                JavaRequirementKind.Mod,
                $"mod:{Path.GetFileName(file)}",
                JavaRequirementEvidence.ModClassFileLowerBound,
                $"Mod {Path.GetFileName(file)} 的 class 文件要求至少 Java {majors[file]}（下界证据）",
                majors[file]))
            .ToArray();
    }

    private int GetJavaMajor(string jarPath, CancellationToken cancellationToken, out bool updated)
    {
        updated = false;
        var info = new FileInfo(jarPath);
        try
        {
            if (_cache.TryGetValue(jarPath, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.JavaMajor;
            }

            var major = ReadClassFileJavaMajor(jarPath, cancellationToken);
            _cache[jarPath] = new CacheEntry(info.Length, info.LastWriteTimeUtc, major);
            updated = true;
            return major;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
            NotSupportedException or ArgumentException)
        {
            _logger.LogDebug(exception, "读取 Mod {File} 的 class 版本失败，已跳过。", Path.GetFileName(jarPath));
            try
            {
                _cache[jarPath] = new CacheEntry(info.Length, info.LastWriteTimeUtc, 0);
            }
            catch (Exception cacheException) when (
                cacheException is IOException or UnauthorizedAccessException)
            {
                _cache[jarPath] = new CacheEntry(-1, DateTime.MinValue, 0);
            }

            updated = true;
            return 0;
        }
    }

    private static int ReadClassFileJavaMajor(string jarPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var maxClassMajor = 0;
        var inspected = 0;
        Span<byte> header = stackalloc byte[ClassFileHeaderLength];

        foreach (var entry in archive.Entries)
        {
            if (inspected >= MaxClassEntriesPerJar)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("META-INF/versions/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Name, "module-info.class", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            inspected++;
            using var stream = entry.Open();
            header.Clear();
            var read = stream.ReadAtLeast(header, ClassFileHeaderLength, throwOnEndOfStream: false);
            if (read < ClassFileHeaderLength ||
                BinaryPrimitives.ReadUInt32BigEndian(header[..4]) != 0xCAFEBABE)
            {
                continue;
            }

            var classMajor = BinaryPrimitives.ReadUInt16BigEndian(header[6..8]);
            if (classMajor >= FirstClassMajor)
            {
                maxClassMajor = Math.Max(maxClassMajor, classMajor);
            }
        }

        if (maxClassMajor == 0)
        {
            return 0;
        }

        // class file 45–48 对应 Java 1.1–1.4；Minecraft 启动下限仍按 Java 8 处理。
        return maxClassMajor <= 48 ? 8 : maxClassMajor - 44;
    }

    private void EnsureCacheLoaded()
    {
        lock (_cacheGate)
        {
            if (_cacheLoaded)
            {
                return;
            }

            _cacheLoaded = true;
            var path = CachePath;
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var content = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheFileEntry>>(content, EclJson.Default);
                if (entries is null)
                {
                    return;
                }

                foreach (var (key, value) in entries)
                {
                    _cache[key] = new CacheEntry(
                        value.Length,
                        new DateTime(value.LastWriteUtcTicks, DateTimeKind.Utc),
                        value.JavaMajor);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogDebug(exception, "读取 Mod Java 要求缓存失败，将重新扫描。");
            }
        }
    }

    private void SaveCache()
    {
        lock (_cacheGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var entries = _cache.ToDictionary(
                    pair => pair.Key,
                    pair => new CacheFileEntry(
                        pair.Value.Length,
                        pair.Value.LastWriteUtc.Ticks,
                        pair.Value.JavaMajor),
                    StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(entries, EclJson.Indented));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "写入 Mod Java 要求缓存失败，不影响本次选择。");
            }
        }
    }

    private string CachePath => Path.Combine(_paths.CacheDirectory, "java", "mod-requirements.json");

    private readonly record struct CacheEntry(long Length, DateTime LastWriteUtc, int JavaMajor);

    private sealed record CacheFileEntry(long Length, long LastWriteUtcTicks, int JavaMajor);
}
