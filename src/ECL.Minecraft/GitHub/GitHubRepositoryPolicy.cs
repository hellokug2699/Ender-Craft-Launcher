// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.GitHub;

/// <summary>
/// 固定仓库边界与 GitHub 引用解析（ADR 0049 §4/§5/§6）。所有入口必须使用同一解析器；
/// 只允许目标公开仓库内已实现原生视图的资源类型，不把 Markdown URL 直接作为请求目标。
/// </summary>
public static partial class GitHubRepositoryPolicy
{
    public static GitHubRepository Target { get; } = new("hellokug2699", "Ender-Craft-Launcher");

    public const string WebHost = "github.com";

    public const string ApiHost = "api.github.com";

    public static bool TryParse(
        string? reference,
        GitHubResourceRef? context,
        out GitHubResourceRef result,
        out string error)
    {
        result = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
        {
            error = "引用为空。";
            return false;
        }

        var raw = reference.Trim();
        if (raw.Any(character => character < 0x20))
        {
            error = "引用包含控制字符。";
            return false;
        }

        var lowerRaw = raw.ToLowerInvariant();
        if (lowerRaw.Contains("%2e") || lowerRaw.Contains("%2f") || lowerRaw.Contains("%5c") ||
            lowerRaw.Contains("%00"))
        {
            error = "引用包含编码后的路径穿越或分隔符。";
            return false;
        }

        if (raw.StartsWith('#'))
        {
            if (context is null)
            {
                error = "片段引用缺少上下文。";
                return false;
            }

            var anchor = raw[1..];
            result = context with { Anchor = anchor, Original = raw };
            return true;
        }

        Uri uri;
        var isRelative = false;
        try
        {
            if (raw.StartsWith("//", StringComparison.Ordinal))
            {
                error = "拒绝协议相对 URL。";
                return false;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            {
                if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(absolute.Host, WebHost, StringComparison.OrdinalIgnoreCase))
                {
                    error = "只允许固定 GitHub 仓库的 HTTPS 链接。";
                    return false;
                }

                uri = absolute;
            }
            else if (raw.StartsWith('/'))
            {
                uri = new Uri($"https://{WebHost}{raw}");
            }
            else
            {
                if (context is null)
                {
                    error = "相对引用缺少资源上下文。";
                    return false;
                }

                isRelative = true;
                var basePath = context.Kind is GitHubResourceKind.File or GitHubResourceKind.Directory
                    ? GetDirectory(context.Path)
                    : string.Empty;
                var baseUri = new Uri($"https://{WebHost}/{Target.Owner}/{Target.Name}/{basePath}");
                uri = new Uri(baseUri, raw);
                if (!string.Equals(uri.Host, WebHost, StringComparison.OrdinalIgnoreCase))
                {
                    error = "相对引用解析到固定仓库之外。";
                    return false;
                }
            }
        }
        catch (UriFormatException)
        {
            error = "引用不是合法 URL。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort && uri.Port != 443 ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "引用包含不支持的 authority 或 scheme。";
            return false;
        }

        if (!TryGetSafeSegments(uri.AbsolutePath, out var segments, out error))
        {
            return false;
        }

        if (segments.Count < 3 ||
            !string.Equals(segments[0], Target.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], Target.Name, StringComparison.OrdinalIgnoreCase))
        {
            error = $"引用不属于固定仓库 {Target.Slug}。";
            return false;
        }

        var rest = segments.Skip(2).ToArray();
        if (isRelative)
        {
            // Markdown 相对链接指向仓库内文件/目录，不按 Issue/PR 等路由解释。
            if (rest.Length == 0)
            {
                error = "相对引用只指向仓库根目录。";
                return false;
            }

            var relativePath = string.Join('/', rest);
            result = new GitHubResourceRef(
                raw.EndsWith('/') ? GitHubResourceKind.Directory : GitHubResourceKind.File,
                Target.Owner,
                Target.Name,
                string.Empty,
                relativePath,
                string.IsNullOrWhiteSpace(uri.Fragment) ? null : uri.Fragment.TrimStart('#'),
                raw);
            return true;
        }

        if (!TryMap(rest, uri.Fragment, raw, out result, out error))
        {
            return false;
        }

        return true;
    }

    /// <summary>验证 GitHub API 请求只发往固定主机与目标仓库路径；GraphQL 只允许固定 endpoint。</summary>
    public static bool IsAllowedApiUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.IsDefaultPort == false && uri.Port != 443)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (string.Equals(path, "/graphql", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = $"/repos/{Target.Owner}/{Target.Name}";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               (path.Length == prefix.Length || path[prefix.Length] == '/');
    }

    public static bool TryParseApiResourceUrl(
        string? url,
        out GitHubResourceRef result,
        out string error)
    {
        result = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !IsAllowedApiUri(uri))
        {
            error = "不是受控的 GitHub API 资源链接。";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 || segments[0] != "repos")
        {
            error = "API 资源路径不受支持。";
            return false;
        }

        var rest = segments.Skip(3).ToArray();
        return TryMap(rest, uri.Fragment, url, out result, out error);
    }

    /// <summary>将 GitHub Markdown/相对引用解析为原生资源；无法解析时返回 false 且不提供放行入口。</summary>
    public static IReadOnlyList<GitHubResolvedLink> ResolveMarkdownLinks(
        string markdown,
        GitHubResourceRef? context)
    {
        var links = new List<GitHubResolvedLink>();
        foreach (var match in MarkdownLinkRegex().Matches(markdown).Cast<System.Text.RegularExpressions.Match>())
        {
            var text = match.Groups["text"].Value;
            var target = match.Groups["target"].Value.Trim();
            var isImage = match.Value.StartsWith('!');
            if (TryParse(target, context, out var resource, out var error))
            {
                links.Add(new GitHubResolvedLink(target, text, isImage, resource, null));
            }
            else
            {
                links.Add(new GitHubResolvedLink(target, text, isImage, null, error));
            }
        }

        return links;
    }

    private static bool TryMap(
        IReadOnlyList<string> rest,
        string fragment,
        string original,
        out GitHubResourceRef result,
        out string error)
    {
        result = null!;
        error = string.Empty;
        var anchor = fragment.TrimStart('#');
        if (anchor.Length > 200)
        {
            error = "片段标识过长。";
            return false;
        }

        GitHubResourceKind kind;
        string identifier;
        string? path = null;
        switch (rest.Count)
        {
            case 0:
                error = "引用只指向仓库首页，不是受支持的原生资源。";
                return false;
            case >= 2 when rest[0].Equals("issues", StringComparison.OrdinalIgnoreCase):
                if (!int.TryParse(rest[1], out var issueNumber) || issueNumber <= 0)
                {
                    error = "Issue 编号不合法。";
                    return false;
                }

                kind = GitHubResourceKind.Issue;
                identifier = issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case >= 2 when rest[0].Equals("pull", StringComparison.OrdinalIgnoreCase) ||
                            rest[0].Equals("pulls", StringComparison.OrdinalIgnoreCase):
                if (!int.TryParse(rest[1], out var pullNumber) || pullNumber <= 0)
                {
                    error = "Pull Request 编号不合法。";
                    return false;
                }

                kind = GitHubResourceKind.PullRequest;
                identifier = pullNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case >= 2 when rest[0].Equals("commit", StringComparison.OrdinalIgnoreCase):
                if (!rest[1].All(Uri.IsHexDigit) || rest[1].Length is < 7 or > 40)
                {
                    error = "Commit SHA 不合法。";
                    return false;
                }

                kind = GitHubResourceKind.Commit;
                identifier = rest[1].ToLowerInvariant();
                break;
            case >= 2 when rest[0].Equals("tree", StringComparison.OrdinalIgnoreCase):
                kind = GitHubResourceKind.Directory;
                identifier = rest[1];
                path = rest.Count > 2 ? string.Join('/', rest.Skip(2)) : string.Empty;
                break;
            case >= 2 when rest[0].Equals("blob", StringComparison.OrdinalIgnoreCase):
                kind = GitHubResourceKind.File;
                identifier = rest[1];
                path = rest.Count > 2 ? string.Join('/', rest.Skip(2)) : string.Empty;
                if (path.Length == 0)
                {
                    error = "文件引用缺少路径。";
                    return false;
                }

                break;
            case >= 2 when rest[0].Equals("releases", StringComparison.OrdinalIgnoreCase):
                if (rest[1].Equals("tag", StringComparison.OrdinalIgnoreCase) && rest.Count >= 3)
                {
                    kind = GitHubResourceKind.Release;
                    identifier = string.Join('/', rest.Skip(2));
                }
                else if (rest[1].Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    kind = GitHubResourceKind.Release;
                    identifier = "latest";
                }
                else
                {
                    error = "Release 引用不受支持。";
                    return false;
                }

                break;
            case >= 2 when rest[0].Equals("discussions", StringComparison.OrdinalIgnoreCase):
                if (!int.TryParse(rest[1], out var discussionNumber) || discussionNumber <= 0)
                {
                    error = "Discussion 编号不合法。";
                    return false;
                }

                kind = GitHubResourceKind.Discussion;
                identifier = discussionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case >= 2 when rest[0].Equals("tags", StringComparison.OrdinalIgnoreCase):
                kind = GitHubResourceKind.Tag;
                identifier = string.Join('/', rest.Skip(1));
                break;
            default:
                error = "不支持的仓库资源类型。";
                return false;
        }

        result = new GitHubResourceRef(
            kind,
            Target.Owner,
            Target.Name,
            identifier,
            path,
            string.IsNullOrWhiteSpace(anchor) ? null : anchor,
            original);
        return true;
    }

    private static bool TryGetSafeSegments(
        string absolutePath,
        out IReadOnlyList<string> segments,
        out string error)
    {
        segments = [];
        error = string.Empty;
        var lower = absolutePath.ToLowerInvariant();
        if (lower.Contains("%2f") || lower.Contains("%5c") || lower.Contains("%00"))
        {
            error = "引用包含编码后的路径分隔符。";
            return false;
        }

        var decoded = Uri.UnescapeDataString(absolutePath);
        if (decoded.Contains('\\') || decoded.Contains('\0') || decoded.Contains("//", StringComparison.Ordinal))
        {
            error = "引用包含非法路径字符或重复分隔符。";
            return false;
        }

        var parts = decoded.Split('/');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (part is "." or "..")
            {
                error = "引用包含路径穿越段。";
                return false;
            }

            if (part.Contains(':') || part.EndsWith('.') || part.EndsWith(' '))
            {
                error = "引用包含歧义路径段。";
                return false;
            }

            result.Add(part);
        }

        segments = result;
        return true;
    }

    private static string GetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..(index + 1)];
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"!?\[(?<text>[^\]\r\n]{0,500})\]\((?<target>[^)\s]{1,2000})\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MarkdownLinkRegex();
}
