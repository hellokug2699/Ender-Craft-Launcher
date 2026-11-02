// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Wardrobe.Png;

namespace Ecl.Wardrobe;

/// <summary>
/// 删除前检查资源引用。返回账户/实例等引用方标识（例如 <c>account:default</c>、<c>instance:survival</c>）。
/// 返回空集合表示可以删除。回调必须是只读的，不得修改衣柜。
/// </summary>
public delegate IReadOnlyList<string> WardrobeReferenceInspector(WardrobeItem item);

/// <summary>
/// 本地衣柜存储。索引（index.json）只保存资源引用和非敏感元数据，资源文件位于
/// <c>textures/</c> 下，文件名使用内容 SHA-256。
/// <para>
/// 设计约束（ADR 0037）：删除账户或实例不会自动删除衣柜资源；只有用户显式删除衣柜项时才删除文件，
/// 并且删除前必须通过引用检查回调确认没有账户/实例仍在使用该资源。
/// </para>
/// </summary>
public sealed class WardrobeStore
{
    public const string IndexFileName = "index.json";
    public const string TexturesDirectoryName = "textures";

    private static readonly JsonSerializerOptions JsonOptions = EclJson.Indented;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDirectory;
    private readonly string _indexPath;
    private readonly string _texturesDirectory;

    public WardrobeStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("衣柜根目录不能为空。", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _indexPath = Path.Combine(_rootDirectory, IndexFileName);
        _texturesDirectory = Path.Combine(_rootDirectory, TexturesDirectoryName);
    }

    public string RootDirectory => _rootDirectory;

    public string IndexPath => _indexPath;

    public string TexturesDirectory => _texturesDirectory;

    /// <summary>读取索引；文件缺失返回空索引，文件损坏时备份原文件并返回空索引而不是抛异常。</summary>
    public WardrobeIndexLoadResult LoadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            return new WardrobeIndexLoadResult { Index = new WardrobeIndex() };
        }

        try
        {
            using var stream = File.OpenRead(_indexPath);
            var index = JsonSerializer.Deserialize<WardrobeIndex>(stream, EclJson.Default)
                ?? throw new JsonException("衣柜索引为空。");

            var warning = Sanitize(index);
            return new WardrobeIndexLoadResult
            {
                Index = index,
                Warning = warning,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            BackupCorruptIndex();
            return new WardrobeIndexLoadResult
            {
                Index = new WardrobeIndex(),
                RecoveredFromCorruption = true,
                Warning = $"衣柜索引损坏，已备份原文件并按空索引继续：{exception.Message}",
            };
        }
    }

    /// <summary>按 ID 查找衣柜项；索引损坏时返回 null，不抛异常。</summary>
    public WardrobeItem? GetItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        return LoadIndex().Index.Items.FirstOrDefault(
            item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
    }

    /// <summary>按内容哈希查找衣柜项（大小写不敏感）。</summary>
    public WardrobeItem? FindBySha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        return LoadIndex().Index.Items.FirstOrDefault(
            item => string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 导入本地 PNG。先做尺寸/像素/文件大小与解码校验，再按 SHA-256 去重；
    /// 相同内容返回已有条目，不重复写文件或索引。
    /// </summary>
    public async Task<Result<WardrobeImportResult>> ImportAsync(
        string sourceFilePath,
        WardrobeTextureType type,
        string? name = null,
        bool isFavorite = false,
        PngValidationLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(type))
        {
            return Result<WardrobeImportResult>.Fail("wardrobe.invalid_type", "未知的衣柜资源类型。");
        }

        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return Result<WardrobeImportResult>.Fail("wardrobe.source_missing", "请选择要导入的 PNG 文件。");
        }

        limits ??= PngValidationLimits.Default;
        var sourcePath = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(sourcePath))
        {
            return Result<WardrobeImportResult>.Fail("wardrobe.source_missing", "要导入的 PNG 文件不存在。");
        }

        byte[] bytes;
        try
        {
            var length = new FileInfo(sourcePath).Length;
            if (length > limits.MaxFileBytes)
            {
                return Result<WardrobeImportResult>.Fail(
                    "wardrobe.file_too_large",
                    $"PNG 文件超过 {limits.MaxFileBytes} 字节上限。");
            }

            bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<WardrobeImportResult>.Fail(
                "wardrobe.read_failed",
                $"读取 PNG 文件失败：{exception.Message}");
        }

        var validation = PngValidator.Validate(bytes, limits);
        if (!validation.IsValid)
        {
            var code = validation.Status == PngValidationStatus.UnsupportedFormat
                ? "wardrobe.unsupported_png"
                : "wardrobe.invalid_png";
            return Result<WardrobeImportResult>.Fail(
                code,
                validation.Message,
                $"PNG 校验状态：{validation.Status}");
        }

        var image = validation.Image!;
        var sha256 = ComputeSha256(bytes);
        var model = type == WardrobeTextureType.Skin
            ? SkinModelHeuristic.Infer(image).Model
            : SkinModel.Unknown;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = LoadIndex().Index;
            var existing = index.Items.FirstOrDefault(
                item => string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return Result<WardrobeImportResult>.Ok(new WardrobeImportResult
                {
                    Item = existing,
                    Created = false,
                });
            }

            Directory.CreateDirectory(_texturesDirectory);
            var relativePath = $"{TexturesDirectoryName}/{sha256}.png";
            var destination = ResolveRelativePath(relativePath);
            if (!File.Exists(destination))
            {
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
            }

            var displayName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : name.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = type == WardrobeTextureType.Skin ? "未命名皮肤" : "未命名披风";
            }

            var item = new WardrobeItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = type,
                RelativePath = relativePath,
                Sha256 = sha256,
                Width = image.Width,
                Height = image.Height,
                SkinModel = model,
                Name = displayName,
                IsFavorite = isFavorite,
                ImportedAt = DateTimeOffset.UtcNow,
            };

            index.Items.Add(item);
            SaveIndex(index);

            return Result<WardrobeImportResult>.Ok(new WardrobeImportResult
            {
                Item = item,
                Created = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<WardrobeImportResult>.Fail(
                "wardrobe.save_failed",
                $"保存衣柜资源失败：{exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>导出资源到用户选择的位置。默认不覆盖已有文件，原资源保持不变。</summary>
    public async Task<Result> ExportAsync(
        string itemId,
        string destinationFilePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFilePath))
        {
            return Result.Fail("wardrobe.invalid_destination", "导出目标路径不能为空。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = LoadIndex().Index.Items.FirstOrDefault(
                candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return Result.Fail("wardrobe.item_not_found", "衣柜中不存在该资源。");
            }

            var source = ResolveRelativePath(item.RelativePath);
            if (!File.Exists(source))
            {
                return Result.Fail("wardrobe.resource_missing", "衣柜资源文件缺失，无法导出。");
            }

            var destination = Path.GetFullPath(destinationFilePath);
            if (!overwrite && File.Exists(destination))
            {
                return Result.Fail("wardrobe.destination_exists", "导出目标文件已存在。");
            }

            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(source, destination, overwrite);
                return Result.Ok();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Fail("wardrobe.export_failed", $"导出衣柜资源失败：{exception.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 删除衣柜项。删除前调用 <paramref name="referenceInspector"/> 检查账户/实例引用；
    /// 存在引用时拒绝删除并返回引用方，绝不级联修改账户或实例数据。
    /// </summary>
    public async Task<Result> DeleteAsync(
        string itemId,
        WardrobeReferenceInspector? referenceInspector = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Result.Fail("wardrobe.item_not_found", "衣柜中不存在该资源。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = LoadIndex().Index;
            var item = index.Items.FirstOrDefault(
                candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return Result.Fail("wardrobe.item_not_found", "衣柜中不存在该资源。");
            }

            IReadOnlyList<string> references = [];
            try
            {
                references = referenceInspector?.Invoke(item) ?? [];
            }
            catch (Exception exception)
            {
                return Result.Fail(
                    "wardrobe.reference_check_failed",
                    $"检查资源引用失败：{exception.Message}");
            }

            var actualReferences = references
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (actualReferences.Count > 0)
            {
                return Result.Fail(
                    "wardrobe.item_referenced",
                    "该资源仍被账户或实例引用，不能删除。",
                    string.Join("；", actualReferences));
            }

            index.Items.Remove(item);
            try
            {
                SaveIndex(index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return Result.Fail("wardrobe.save_failed", $"保存衣柜索引失败：{exception.Message}");
            }

            // 索引已经不再引用该文件；文件删除失败会留下孤儿文件，但不会影响其他资源。
            try
            {
                var resource = ResolveRelativePath(item.RelativePath);
                if (File.Exists(resource))
                {
                    File.Delete(resource);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Fail(
                    "wardrobe.resource_delete_failed",
                    $"衣柜项已从索引移除，但资源文件删除失败：{exception.Message}");
            }

            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>更新收藏状态；返回更新后的条目。</summary>
    public async Task<Result<WardrobeItem>> SetFavoriteAsync(
        string itemId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = LoadIndex().Index;
            var itemIndex = index.Items.FindIndex(
                item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (itemIndex < 0)
            {
                return Result<WardrobeItem>.Fail("wardrobe.item_not_found", "衣柜中不存在该资源。");
            }

            var updated = index.Items[itemIndex] with { IsFavorite = isFavorite };
            index.Items[itemIndex] = updated;

            try
            {
                SaveIndex(index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return Result<WardrobeItem>.Fail("wardrobe.save_failed", $"保存衣柜索引失败：{exception.Message}");
            }

            return Result<WardrobeItem>.Ok(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把索引中的相对路径解析为衣柜根目录下的绝对路径，并阻止路径越界。</summary>
    public string ResolveResourcePath(WardrobeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ResolveRelativePath(item.RelativePath);
    }

    public static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private string ResolveRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));
        var relative = Path.GetRelativePath(_rootDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("衣柜资源路径越界。");
        }

        return fullPath;
    }

    private void SaveIndex(WardrobeIndex index)
    {
        Directory.CreateDirectory(_rootDirectory);

        var temporaryPath = $"{_indexPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(index, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // 临时文件清理失败不影响索引写入结果。
                }
            }
        }
    }

    private void BackupCorruptIndex()
    {
        try
        {
            var backup = $"{_indexPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
            File.Copy(_indexPath, backup, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 备份失败不应阻止衣柜以降级模式继续工作。
        }
    }

    /// <summary>丢弃明显无效的索引项并返回警告；单个坏条目不应让整个衣柜不可用。</summary>
    private static string? Sanitize(WardrobeIndex index)
    {
        var source = index.Items;
        if (source is null)
        {
            source = [];
        }

        var valid = new List<WardrobeItem>(source.Count);
        var dropped = 0;

        foreach (var item in source)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Sha256) ||
                item.Sha256.Length != 64 ||
                string.IsNullOrWhiteSpace(item.Name) ||
                !IsSafeRelativePath(item.RelativePath) ||
                !Enum.IsDefined(item.Type) ||
                !Enum.IsDefined(item.SkinModel))
            {
                dropped++;
                continue;
            }

            valid.Add(item);
        }

        index.Items = valid;
        return dropped == 0 ? null : $"衣柜索引中有 {dropped} 个无效条目已被忽略。";
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
               segments.All(segment => segment is not "." and not "..");
    }
}
