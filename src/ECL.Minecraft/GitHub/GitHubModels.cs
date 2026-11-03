// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.GitHub;

public enum GitHubResourceKind
{
    Issue,
    PullRequest,
    Commit,
    Branch,
    Tag,
    File,
    Directory,
    Release,
    Discussion,
}

/// <summary>
/// 通过固定仓库边界解析出的原生资源引用。它只包含资源类型与标识，不包含任意请求 URL。
/// </summary>
public sealed record GitHubResourceRef(
    GitHubResourceKind Kind,
    string Owner,
    string Repository,
    string Identifier,
    string? Path = null,
    string? Anchor = null,
    string? Original = null)
{
    public string NativeRoute => Kind switch
    {
        GitHubResourceKind.Issue => $"github://repository/issues/{Identifier}{AnchorSuffix}",
        GitHubResourceKind.PullRequest => $"github://repository/pulls/{Identifier}{AnchorSuffix}",
        GitHubResourceKind.Commit => $"github://repository/commits/{Identifier}",
        GitHubResourceKind.File => $"github://repository/files/{Path}",
        GitHubResourceKind.Directory => $"github://repository/tree/{Path}",
        GitHubResourceKind.Release => $"github://repository/releases/{Identifier}",
        GitHubResourceKind.Discussion => $"github://repository/discussions/{Identifier}{AnchorSuffix}",
        GitHubResourceKind.Branch or GitHubResourceKind.Tag => $"github://repository/refs/{Identifier}",
        _ => "github://repository",
    };

    private string AnchorSuffix => string.IsNullOrWhiteSpace(Anchor) ? string.Empty : $"#{Anchor}";
}

public sealed record GitHubRepository(string Owner, string Name)
{
    public string Slug => $"{Owner}/{Name}";
}

public sealed record GitHubResolvedLink(
    string Original,
    string Text,
    bool IsImage,
    GitHubResourceRef? Resource,
    string? Rejection);

public sealed record GitHubRelease(
    long Id,
    string TagName,
    string Name,
    string? Body,
    bool Draft,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    string HtmlUrl);

public sealed record GitHubIssue(
    long Id,
    int Number,
    string Title,
    string? Body,
    string State,
    string AuthorLogin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string HtmlUrl,
    bool IsPullRequest);

public sealed record GitHubComment(
    long Id,
    string Body,
    string AuthorLogin,
    DateTimeOffset CreatedAt,
    string HtmlUrl);

public sealed record GitHubDiscussion(
    string Id,
    int Number,
    string Title,
    string? Body,
    string Category,
    string AuthorLogin,
    string HtmlUrl);

public sealed record GitHubApiResult<T>(
    T? Value,
    bool FromCache,
    DateTimeOffset? LastModified,
    string? ETag);
