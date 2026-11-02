// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecl.World.Nbt;

namespace Ecl.World.Semantics;

public sealed record LocalizedText
{
    public string ZhCn { get; init; } = string.Empty;

    public string En { get; init; } = string.Empty;

    public string Get(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ZhCn))
            {
                return ZhCn;
            }

            if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(En))
            {
                return En;
            }
        }

        if (!string.IsNullOrWhiteSpace(ZhCn))
        {
            return ZhCn;
        }

        return En;
    }
}

public sealed record NbtSemanticDefinition
{
    public required string Id { get; init; }

    public IReadOnlyList<string> FileKinds { get; init; } = [];

    public string? PathPattern { get; init; }

    public string? TagName { get; init; }

    public IReadOnlyList<string> Types { get; init; } = [];

    public int DataVersionMin { get; init; }

    public int DataVersionMax { get; init; } = int.MaxValue;

    public LocalizedText DisplayName { get; init; } = new();

    public LocalizedText Summary { get; init; } = new();

    public LocalizedText Details { get; init; } = new();

    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public string Source { get; init; } = string.Empty;

    public string SourceVersion { get; init; } = string.Empty;

    public string Verification { get; init; } = "unverified";

    public string License { get; init; } = string.Empty;

    public bool Verified => string.Equals(Verification, "verified", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public Regex? PathRegex { get; private set; }

    public NbtSemanticDefinition Compile()
    {
        if (!string.IsNullOrWhiteSpace(PathPattern))
        {
            PathRegex = new Regex(PathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }

        return this;
    }
}

public sealed record NbtSemanticMatch(
    NbtSemanticDefinition Definition,
    string Path,
    bool MatchedPath,
    int Specificity);

public sealed record NbtSemanticCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string CatalogVersion { get; init; } = "0";

    public string Source { get; init; } = string.Empty;

    public IReadOnlyList<NbtSemanticDefinition> Definitions { get; init; } = [];
}

/// <summary>
/// 版本化 NBT 语义目录。定义按文件类型、DataVersion、完整路径/标签名匹配；
/// 同名标签在不同上下文有不同定义时返回全部候选，由调用方展示匹配依据。
/// </summary>
public sealed class NbtSemanticCatalog
{
    public const string DefaultResourceName = "Ecl.World.Semantic.nbt-semantics.json";

    private NbtSemanticCatalog(NbtSemanticCatalogDocument document)
    {
        Document = document;
        Definitions = document.Definitions.Select(definition => definition.Compile()).ToArray();
    }

    public NbtSemanticCatalogDocument Document { get; }

    public IReadOnlyList<NbtSemanticDefinition> Definitions { get; }

    public string CatalogVersion => Document.CatalogVersion;

    public static NbtSemanticCatalog Load(Stream stream)
    {
        var document = JsonSerializer.Deserialize<NbtSemanticCatalogDocument>(
                           stream,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true,
                               PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                               Converters = { new JsonStringEnumConverter() },
                           })
                       ?? throw new InvalidDataException("NBT 语义目录为空。");
        if (document.SchemaVersion > 1)
        {
            throw new InvalidDataException($"不支持的 NBT 语义目录版本：{document.SchemaVersion}。");
        }

        return new NbtSemanticCatalog(document);
    }

    public static NbtSemanticCatalog LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static NbtSemanticCatalog LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultResourceName)
                           ?? throw new InvalidOperationException("内嵌 NBT 语义目录缺失。");
        return Load(stream);
    }

    public IReadOnlyList<NbtSemanticMatch> Query(
        string fileKind,
        int dataVersion,
        string path,
        string tagName,
        string? language = null)
    {
        var normalized = NormalizePath(path);
        var matches = new List<NbtSemanticMatch>();
        foreach (var definition in Definitions)
        {
            if (definition.FileKinds.Count > 0 &&
                !definition.FileKinds.Any(kind => string.Equals(kind, fileKind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (dataVersion < definition.DataVersionMin || dataVersion > definition.DataVersionMax)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(definition.TagName) &&
                !string.Equals(definition.TagName, tagName, StringComparison.Ordinal))
            {
                continue;
            }

            var pathMatched = definition.PathRegex is null ||
                              definition.PathRegex.IsMatch(normalized);
            if (!pathMatched)
            {
                continue;
            }

            var specificity = 0;
            if (definition.PathPattern is not null)
            {
                specificity += 10;
            }

            if (definition.TagName is not null)
            {
                specificity += 2;
            }

            if (definition.FileKinds.Count > 0)
            {
                specificity += 1;
            }

            matches.Add(new NbtSemanticMatch(definition, normalized, definition.PathPattern is not null, specificity));
        }

        return matches
            .OrderByDescending(match => match.Specificity)
            .ThenBy(match => match.Definition.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public string DisplayName(NbtSemanticDefinition definition, string? language) =>
        definition.DisplayName.Get(language) is { Length: > 0 } value ? value : definition.Id;

    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().Trim('/');
}

/// <summary>
/// 编辑会话。跟踪相对打开时基线的直接变更、祖先聚合标记、撤销/重做与语义校验；
/// 所有状态只存在于内存，不写入 NBT 文件。
/// </summary>
public sealed class NbtEditSession
{
    private readonly NbtTag _baseline;
    private readonly Stack<NbtTag> _undo = [];
    private readonly Stack<NbtTag> _redo = [];
    private readonly List<NbtRenameRecord> _renames = [];
    private NbtTag _root;

    public NbtEditSession(NbtTag root)
    {
        if (root.Type != NbtTagType.Compound)
        {
            throw new ArgumentException("编辑会话根标签必须是复合标签。", nameof(root));
        }

        _root = root;
        _baseline = root.DeepClone();
    }

    public NbtTag Root => _root;

    public bool HasChanges => Changes.Count > 0;

    public IReadOnlyList<NbtChange> Changes => Diff();

    public IReadOnlyList<string> ChangedPaths => Changes.Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>存在变更后代的祖先路径（不含直接变更节点本身）。</summary>
    public IReadOnlyList<string> AggregatedAncestorPaths
    {
        get
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in Changes)
            {
                foreach (var ancestor in Ancestors(change.Path))
                {
                    if (!ChangedPaths.Contains(ancestor, StringComparer.Ordinal))
                    {
                        result.Add(ancestor);
                    }
                }
            }

            return result.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
    }

    public bool CanSave(IReadOnlyList<NbtValidationIssue> issues) =>
        issues.All(issue => issue.Severity != NbtValidationSeverity.Error);

    public void Set(string path, NbtTag value)
    {
        var normalized = NbtSemanticCatalog.NormalizePath(path);
        var (parent, name, index) = ResolveParent(normalized);
        PushUndo();
        if (index is { } listIndex)
        {
            if (listIndex < 0 || listIndex >= parent.Children.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(path), "列表索引越界。");
            }

            ReplaceChild(parent, listIndex, value);
        }
        else
        {
            var existing = parent.Child(name);
            if (existing is null)
            {
                parent.Add(RenameTag(value, name));
            }
            else
            {
                var position = IndexOf(parent, existing);
                ReplaceChild(parent, position, RenameTag(value, name));
            }
        }
    }

    public void Add(string path, NbtTag value)
    {
        var normalized = NbtSemanticCatalog.NormalizePath(path);
        var (parent, name, index) = ResolveParent(normalized);
        PushUndo();
        if (index is not null)
        {
            throw new InvalidOperationException("新增列表元素请使用列表路径。");
        }

        if (parent.Child(name) is not null)
        {
            throw new InvalidOperationException($"路径已存在：{normalized}");
        }

        parent.Add(RenameTag(value, name));
    }

    public void Remove(string path)
    {
        var normalized = NbtSemanticCatalog.NormalizePath(path);
        var (parent, name, index) = ResolveParent(normalized, allowMissingLeaf: true);
        PushUndo();
        if (index is { } listIndex)
        {
            if (listIndex < 0 || listIndex >= parent.Children.Count)
            {
                _undo.Pop();
                throw new ArgumentOutOfRangeException(nameof(path), "列表索引越界。");
            }

            parent.RemoveAt(listIndex);
            return;
        }

        var target = parent.Child(name);
        if (target is null)
        {
            _undo.Pop();
            throw new InvalidOperationException($"路径不存在：{normalized}");
        }

        parent.Remove(target);
    }

    public void Rename(string path, string newName)
    {
        var normalized = NbtSemanticCatalog.NormalizePath(path);
        var (parent, name, _) = ResolveParent(normalized);
        var target = parent.Child(name) ?? throw new InvalidOperationException($"路径不存在：{normalized}");
        if (parent.Child(newName) is not null)
        {
            throw new InvalidOperationException($"同级已存在同名标签：{newName}");
        }

        PushUndo();
        var oldPath = normalized;
        target.Name = newName;
        var newPath = ReplaceLeaf(normalized, newName);
        _renames.Add(new NbtRenameRecord(oldPath, newPath));
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(_root.DeepClone());
        _root = _undo.Pop();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(_root.DeepClone());
        _root = _redo.Pop();
        return true;
    }

    public IReadOnlyList<NbtValidationIssue> Validate(
        NbtSemanticCatalog? catalog,
        string fileKind,
        int dataVersion,
        string? language = null)
    {
        var issues = new List<NbtValidationIssue>();
        var flattened = Flatten(_root).ToArray();
        var duplicatePaths = flattened
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicatePaths)
        {
            issues.Add(new NbtValidationIssue(duplicate, NbtValidationSeverity.Error, "nbt.duplicate", "同级存在重复标签名。"));
        }

        foreach (var (path, tag, parentPath) in flattened)
        {
            if (tag.Type == NbtTagType.Compound &&
                tag.Children.Select(child => child.Name).Distinct(StringComparer.Ordinal).Count() != tag.Children.Count)
            {
                issues.Add(new NbtValidationIssue(path, NbtValidationSeverity.Error, "nbt.duplicate_child", "复合标签包含重复子标签。"));
            }

            if (tag.Type == NbtTagType.List && tag.Children.Any(child => child.Type != tag.ListType))
            {
                issues.Add(new NbtValidationIssue(path, NbtValidationSeverity.Error, "nbt.list_type", "列表元素类型不一致。"));
            }

            if (catalog is null)
            {
                continue;
            }

            var matches = catalog.Query(fileKind, dataVersion, path, tag.Name, language);
            if (matches.Count == 0)
            {
                // 未知标签不能仅因缺少定义而判为非法。
                continue;
            }

            var definition = matches[0].Definition;
            if (definition.Types.Count > 0 &&
                !definition.Types.Any(type => string.Equals(type, tag.Type.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new NbtValidationIssue(
                    path,
                    NbtValidationSeverity.Error,
                    "nbt.type_mismatch",
                    $"{definition.Id} 期望 {string.Join('/', definition.Types)}，实际为 {tag.Type}。"));
            }

            if (definition.AllowedValues.Count > 0 &&
                tag.Value is string stringValue &&
                !definition.AllowedValues.Contains(stringValue, StringComparer.Ordinal))
            {
                issues.Add(new NbtValidationIssue(
                    path,
                    NbtValidationSeverity.Warning,
                    "nbt.value_not_listed",
                    $"{definition.Id} 的已知取值不包含“{stringValue}”；未知取值不一定是错误。"));
            }

            if (tag.Value is IConvertible convertible &&
                (definition.Minimum is not null || definition.Maximum is not null))
            {
                try
                {
                    var numeric = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    if ((definition.Minimum is { } minimum && numeric < minimum) ||
                        (definition.Maximum is { } maximum && numeric > maximum))
                    {
                        issues.Add(new NbtValidationIssue(
                            path,
                            NbtValidationSeverity.Warning,
                            "nbt.range",
                            $"{definition.Id} 建议范围 {definition.Minimum}..{definition.Maximum}，实际为 {numeric}。"));
                    }
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException)
                {
                    // 非数值标签不参与范围校验。
                }
            }
        }

        return issues;
    }

    private void PushUndo()
    {
        _undo.Push(_root.DeepClone());
        _redo.Clear();
    }

    private IReadOnlyList<NbtChange> Diff()
    {
        var baseline = Flatten(_baseline).ToDictionary(item => item.Path, item => item.Tag, StringComparer.Ordinal);
        var current = Flatten(_root).ToDictionary(item => item.Path, item => item.Tag, StringComparer.Ordinal);
        var changes = new List<NbtChange>();
        foreach (var (path, tag) in current)
        {
            if (!baseline.TryGetValue(path, out var old))
            {
                var rename = _renames.LastOrDefault(item => item.NewPath == path);
                changes.Add(new NbtChange(path, NbtChangeKind.Added, rename is null ? null : "renamed", FormatValue(tag), rename?.OldPath));
                continue;
            }

            if (old.Type != tag.Type || !Equals(old.Value, tag.Value) || !string.Equals(old.Name, tag.Name, StringComparison.Ordinal))
            {
                changes.Add(new NbtChange(path, NbtChangeKind.Modified, FormatValue(old), FormatValue(tag)));
            }
        }

        foreach (var path in baseline.Keys.Where(path => !current.ContainsKey(path)))
        {
            var rename = _renames.LastOrDefault(item => item.OldPath == path);
            changes.Add(new NbtChange(path, NbtChangeKind.Removed, FormatValue(baseline[path]), null, rename?.NewPath));
        }

        return changes.OrderBy(change => change.Path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        var normalized = NbtSemanticCatalog.NormalizePath(path);
        for (var index = normalized.LastIndexOf('/'); index > 0; index = normalized.LastIndexOf('/', index - 1))
        {
            yield return normalized[..index];
        }
    }

    private (NbtTag Parent, string Name, int? Index) ResolveParent(string path, bool allowMissingLeaf = false)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("路径为空。", nameof(path));
        }

        var current = _root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (segment.EndsWith(']') && segment.Contains('[', StringComparison.Ordinal))
            {
                var bracket = segment.IndexOf('[', StringComparison.Ordinal);
                var name = segment[..bracket];
                var list = name.Length == 0 ? current : current.Child(name);
                if (list is not { Type: NbtTagType.List } ||
                    !int.TryParse(segment[(bracket + 1)..^1], out var listIndex) ||
                    listIndex < 0 || listIndex >= list.Children.Count)
                {
                    throw new InvalidOperationException($"路径不存在：{path}");
                }

                current = list.Children[listIndex];
                continue;
            }

            current = current.Child(segment) ?? throw new InvalidOperationException($"路径不存在：{path}");
        }

        var leaf = segments[^1];
        if (leaf.EndsWith(']') && leaf.Contains('[', StringComparison.Ordinal))
        {
            var bracket = leaf.IndexOf('[', StringComparison.Ordinal);
            var name = leaf[..bracket];
            var list = name.Length == 0 ? current : current.Child(name);
            if (list is not { Type: NbtTagType.List } ||
                !int.TryParse(leaf[(bracket + 1)..^1], out var listIndex))
            {
                throw new InvalidOperationException($"路径不存在：{path}");
            }

            return (list, string.Empty, listIndex);
        }

        if (current.Type != NbtTagType.Compound)
        {
            throw new InvalidOperationException($"父级不是复合标签：{path}");
        }

        return (current, leaf, null);
    }

    private static NbtTag RenameTag(NbtTag tag, string name)
    {
        if (string.Equals(tag.Name, name, StringComparison.Ordinal))
        {
            return tag;
        }

        var clone = tag.DeepClone();
        clone.Name = name;
        return clone;
    }

    private static void ReplaceChild(NbtTag parent, int index, NbtTag value)
    {
        var children = parent.Children.ToList();
        children[index] = value;
        parent.ReplaceChildren(children);
    }

    private static int IndexOf(NbtTag parent, NbtTag target) => parent.Children.ToList().IndexOf(target);

    private static string ReplaceLeaf(string path, string newName)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? newName : $"{path[..index]}/{newName}";
    }

    private static IEnumerable<(string Path, NbtTag Tag, string ParentPath)> Flatten(NbtTag root)
    {
        var stack = new Stack<(NbtTag Tag, string Path, string ParentPath)>();
        stack.Push((root, string.Empty, string.Empty));
        while (stack.Count > 0)
        {
            var (tag, path, parentPath) = stack.Pop();
            yield return (path, tag, parentPath);
            if (tag.Type == NbtTagType.List)
            {
                for (var index = tag.Children.Count - 1; index >= 0; index--)
                {
                    stack.Push((tag.Children[index], $"{path}[{index}]", path));
                }
            }
            else if (tag.Type == NbtTagType.Compound)
            {
                for (var index = tag.Children.Count - 1; index >= 0; index--)
                {
                    var child = tag.Children[index];
                    var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                    stack.Push((child, childPath, path));
                }
            }
        }
    }

    private static string? FormatValue(NbtTag tag) => tag.Value switch
    {
        null => null,
        byte[] bytes => $"byte[{bytes.Length}]",
        int[] ints => $"int[{ints.Length}]",
        long[] longs => $"long[{longs.Length}]",
        _ => Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private sealed record NbtRenameRecord(string OldPath, string NewPath);
}

public enum NbtChangeKind
{
    Added,
    Removed,
    Modified,
}

public sealed record NbtChange(
    string Path,
    NbtChangeKind Kind,
    string? OldValue,
    string? NewValue,
    string? RelatedPath = null);

public enum NbtValidationSeverity
{
    Warning,
    Error,
}

public sealed record NbtValidationIssue(
    string Path,
    NbtValidationSeverity Severity,
    string Code,
    string Message,
    string? DefinitionId = null);
