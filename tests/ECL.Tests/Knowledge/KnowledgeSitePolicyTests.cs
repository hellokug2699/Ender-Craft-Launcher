// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using ECL.Knowledge;
using Xunit;

namespace Ecl.Tests.Knowledge;

/// <summary>
/// ADR 0036 受限知识浏览器策略的验收测试：白名单、规范化、协议、重定向、
/// 子资源、错误页和只读地址栏。
/// </summary>
public sealed class KnowledgeSitePolicyTests
{
    [Theory]
    [InlineData("https://zh.minecraft.wiki/", "zh.minecraft.wiki")]
    [InlineData("https://static.zh.minecraft.wiki/load.php?lang=zh", "static.zh.minecraft.wiki")]
    [InlineData("https://mcmod.cn/", "mcmod.cn")]
    [InlineData("https://api.mcmod.cn/class/1", "api.mcmod.cn")]
    [InlineData("https://zh.minecraft.wiki:8443/wiki/Crafting", "zh.minecraft.wiki")]
    public void AllowsHttpsInBothDomainTrees(string url, string expectedHost)
    {
        Assert.True(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Error);
        Assert.NotNull(decision.Target);
        Assert.Equal(expectedHost, decision.Target!.Host);
        Assert.Equal(url, decision.Target.AbsoluteUrl);
    }

    [Theory]
    [InlineData("https://ZH.MINECRAFT.WIKI/wiki/Block", "zh.minecraft.wiki")]
    [InlineData("https://zh.minecraft.wiki./wiki/Block", "zh.minecraft.wiki")]
    [InlineData("https://MCMod.CN./index.php", "mcmod.cn")]
    [InlineData("https://ｍｃｍｏｄ.cn/", "mcmod.cn")]
    [InlineData("https://zh。minecraft。wiki/", "zh.minecraft.wiki")]
    public void NormalizesCaseTrailingDotAndIdnBeforeWhitelistCheck(string url, string expectedHost)
    {
        Assert.True(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.Equal(expectedHost, decision.Target!.Host);
    }

    [Theory]
    [InlineData("https://zh.minecraft.wiki.example.com/")]
    [InlineData("https://evil-zh.minecraft.wiki/")]
    [InlineData("https://mcmod.cn.example.org/")]
    [InlineData("https://notmcmod.cn/")]
    [InlineData("https://mcmod.cn.evil.example/")]
    [InlineData("https://evil.example/zh.minecraft.wiki")]
    [InlineData("https://evil.example/?next=https://mcmod.cn/")]
    public void RejectsLookalikeAndExternalHosts(string url)
    {
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Target);
        Assert.Equal(KnowledgeErrorCodes.SiteNotAllowed, decision.Error!.Code);
    }

    [Theory]
    [InlineData("https://zh.minecraft.wiki@evil.example/wiki")]
    [InlineData("https://evil.example@zh.minecraft.wiki/wiki")]
    [InlineData("https://@zh.minecraft.wiki/wiki")]
    public void RejectsUserInfoSpoofing(string url)
    {
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.Equal(KnowledgeErrorCodes.UrlInvalid, decision.Error!.Code);
        Assert.Equal(url, decision.RejectedTarget);
    }

    [Theory]
    [InlineData("http://zh.minecraft.wiki/wiki")]
    [InlineData("http://mcmod.cn/")]
    [InlineData("ftp://zh.minecraft.wiki/file")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:blank")]
    [InlineData("ecl://settings")]
    public void RejectsNonHttpsProtocolsWithoutUpgrading(string url)
    {
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.Equal(KnowledgeErrorCodes.SchemeUnsupported, decision.Error!.Code);
        Assert.Null(decision.Target);
        Assert.Equal(url, decision.RejectedTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/wiki/Crafting")]
    [InlineData("https://zh.minecraft.wiki:not-a-port/")]
    [InlineData("https://zh.minecraft.wiki../")]
    public void RejectsEmptyRelativeOrUnparseableUrls(string url)
    {
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));
        Assert.Equal(KnowledgeErrorCodes.UrlInvalid, decision.Error!.Code);
    }

    [Fact]
    public void RejectsProtocolRelativeUrls()
    {
        // 在 Windows 上 Uri 会把 //host/path 解析成 file: 地址；无论具体形式如何，
        // 非 HTTPS 的输入都必须在导航前被拒绝。
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation("//zh.minecraft.wiki/wiki", out var decision));
        Assert.Equal(KnowledgeErrorCodes.SchemeUnsupported, decision.Error!.Code);
    }

    [Theory]
    [InlineData("zh.minecraft.wiki", true)]
    [InlineData("static.zh.minecraft.wiki", true)]
    [InlineData("mcmod.cn", true)]
    [InlineData("api.mcmod.cn", true)]
    [InlineData("MCMod.CN.", true)]
    [InlineData("ｍｃｍｏｄ.cn", true)]
    [InlineData("zh.minecraft.wiki.example.com", false)]
    [InlineData("evil-zh.minecraft.wiki", false)]
    [InlineData("mcmod.cn.example.org", false)]
    [InlineData("zh.minecraft.wiki:443", false)]
    [InlineData("zh.minecraft.wiki@evil.example", false)]
    [InlineData("", false)]
    public void IsAllowedHostUsesDnsLabelBoundaries(string hostname, bool expected)
    {
        Assert.Equal(expected, KnowledgeSitePolicy.IsAllowedHost(hostname));
    }

    [Theory]
    [InlineData(302, "/wiki/Block", "https://zh.minecraft.wiki/wiki/Crafting_Table", "https://zh.minecraft.wiki/wiki/Block")]
    [InlineData(301, "https://api.mcmod.cn/class/1", "https://zh.minecraft.wiki/wiki/Mod", "https://api.mcmod.cn/class/1")]
    [InlineData(303, "../class/1", "https://mcmod.cn/wiki/foo", "https://mcmod.cn/class/1")]
    [InlineData(307, "//static.zh.minecraft.wiki/a.png", "https://zh.minecraft.wiki/page", "https://static.zh.minecraft.wiki/a.png")]
    [InlineData(308, "?x=1", "https://zh.minecraft.wiki/page?old=2", "https://zh.minecraft.wiki/page?x=1")]
    public void AllowsAllowedRedirectsAndResolvesRelativeLocations(
        int statusCode,
        string location,
        string requestUrl,
        string expectedTarget)
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(statusCode, location, requestUrl);

        Assert.True(decision.IsAllowed);
        Assert.Equal(statusCode, decision.StatusCode);
        Assert.Equal(expectedTarget, decision.Target!.AbsoluteUrl);
        Assert.Equal(expectedTarget, decision.Target.AbsoluteUrl);
    }

    [Fact]
    public void AllowsCrossTreeRedirectAndRevalidatesEveryHop()
    {
        var firstHop = KnowledgeSitePolicy.EvaluateRedirect(
            302,
            "https://api.mcmod.cn/class/1",
            "https://zh.minecraft.wiki/wiki/Mod");

        Assert.True(firstHop.IsAllowed);
        Assert.Equal("api.mcmod.cn", firstHop.Target!.Host);

        var secondHop = KnowledgeSitePolicy.EvaluateRedirect(
            301,
            "https://static.zh.minecraft.wiki/asset.js",
            firstHop.Target.AbsoluteUrl);

        Assert.True(secondHop.IsAllowed);
        Assert.Equal("static.zh.minecraft.wiki", secondHop.Target!.Host);

        var thirdHop = KnowledgeSitePolicy.EvaluateRedirect(
            302,
            "https://evil.example/collect",
            secondHop.Target.AbsoluteUrl);

        Assert.False(thirdHop.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, thirdHop.Error!.Code);
        Assert.Equal(secondHop.Target.AbsoluteUrl, thirdHop.RequestedUrl);
        Assert.Equal(302, thirdHop.StatusCode);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public void RejectsExternalRedirectAndPreservesRealStatusCode(int statusCode)
    {
        const string requestUrl = "https://zh.minecraft.wiki/wiki/Crafting";
        const string location = "https://evil.example/steal?x=1";

        var decision = KnowledgeSitePolicy.EvaluateRedirect(statusCode, location, requestUrl);

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Target);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(statusCode, decision.StatusCode);
        Assert.Equal(requestUrl, decision.RequestedUrl);
        Assert.Equal(location, decision.RejectedTarget);
        Assert.Equal(location, decision.Location);
    }

    [Theory]
    [InlineData(302, null)]
    [InlineData(302, "")]
    [InlineData(302, "   ")]
    [InlineData(307, null)]
    public void RejectsMissingLocation(int statusCode, string? location)
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(
            statusCode,
            location,
            "https://zh.minecraft.wiki/page");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(statusCode, decision.StatusCode);
        Assert.Null(decision.RejectedTarget);
    }

    [Theory]
    [InlineData("https://[invalid-host")]
    [InlineData("http://[::1")]
    [InlineData("https://exa mple.com/")]
    public void RejectsInvalidLocation(string location)
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(
            302,
            location,
            "https://zh.minecraft.wiki/page");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(location.Trim(), decision.RejectedTarget);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(399)]
    public void RejectsNonSupportedRedirectStatusCodes(int statusCode)
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(
            statusCode,
            "https://zh.minecraft.wiki/page",
            "https://zh.minecraft.wiki/old");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(statusCode, decision.StatusCode);
    }

    [Fact]
    public void RejectsRedirectThatDowngradesToHttp()
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(
            301,
            "http://zh.minecraft.wiki/page",
            "https://zh.minecraft.wiki/page");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(301, decision.StatusCode);
        Assert.Equal("http://zh.minecraft.wiki/page", decision.RejectedTarget);
    }

    [Fact]
    public void RejectsRedirectWithUserInfoSpoofing()
    {
        const string location = "https://zh.minecraft.wiki@evil.example/steal";
        var decision = KnowledgeSitePolicy.EvaluateRedirect(
            302,
            location,
            "https://zh.minecraft.wiki/page");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal(location, decision.RejectedTarget);
    }

    [Fact]
    public void RejectsRedirectWhenRequestUrlIsInvalid()
    {
        var decision = KnowledgeSitePolicy.EvaluateRedirect(302, "/next", "not-a-url");

        Assert.False(decision.IsAllowed);
        Assert.Equal(KnowledgeErrorCodes.RedirectBlocked, decision.Error!.Code);
        Assert.Equal("/next", decision.RejectedTarget);
    }

    [Theory]
    [InlineData("https://static.zh.minecraft.wiki/skins/common/images/block.png")]
    [InlineData("https://zh.minecraft.wiki/load.php?lang=zh&modules=site.styles")]
    [InlineData("https://api.mcmod.cn/fonts/site.woff2")]
    [InlineData("https://mcmod.cn/embed/iframe")]
    public void AllowsWhitelistedSubresourcesAndIframes(string url)
    {
        Assert.True(KnowledgeSitePolicy.IsAllowedSubresource(url));
        Assert.True(KnowledgeSitePolicy.ValidateSubresource(url).IsSuccess);
    }

    [Theory]
    [InlineData("https://cdn.example.com/library.js")]
    [InlineData("https://analytics.example.net/collect")]
    [InlineData("http://static.zh.minecraft.wiki/block.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/skins/common/images/block.png")]
    [InlineData("https://zh.minecraft.wiki.example.com/block.png")]
    public void BlocksExternalSubresourcesAndIframes(string url)
    {
        Assert.False(KnowledgeSitePolicy.IsAllowedSubresource(url));
        Assert.False(KnowledgeSitePolicy.ValidateSubresource(url).IsSuccess);
    }

    [Fact]
    public void SubresourceErrorsUseStableCodes()
    {
        Assert.Equal(
            KnowledgeErrorCodes.SiteNotAllowed,
            KnowledgeSitePolicy.ValidateSubresource("https://cdn.example.com/a.js").Error!.Code);

        Assert.Equal(
            KnowledgeErrorCodes.SchemeUnsupported,
            KnowledgeSitePolicy.ValidateSubresource("http://zh.minecraft.wiki/a.png").Error!.Code);
    }

    [Fact]
    public void AddressBarIsReadOnlyAndCopiesFullUrl()
    {
        const string url = "https://zh.minecraft.wiki/wiki/Crafting_Table?oldid=123#History";
        Assert.True(KnowledgeSitePolicy.TryValidateNavigation(url, out var decision));

        var addressBar = KnowledgeAddressBarState.FromTarget(decision.Target!);

        Assert.False(addressBar.CanEdit);
        Assert.True(addressBar.CanCopy);
        Assert.Equal(url, addressBar.Url);
        Assert.Equal(url, addressBar.CopyText);
        Assert.Equal(url, addressBar.DisplayUrl);
    }

    [Fact]
    public void AddressBarTruncatesOnlyVisualTextNotCopyText()
    {
        var longUrl = "https://zh.minecraft.wiki/wiki/" + new string('a', 400);
        var addressBar = KnowledgeAddressBarState.FromUrl(longUrl);

        Assert.False(addressBar.CanEdit);
        Assert.True(addressBar.CanCopy);
        Assert.Equal(longUrl, addressBar.CopyText);
        Assert.Equal(longUrl, addressBar.Url);
        Assert.True(addressBar.DisplayUrl.Length < longUrl.Length);
        Assert.EndsWith("…", addressBar.DisplayUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPageEncodeScriptInputAndContainsNoExternalResources()
    {
        const string maliciousUrl = "https://evil.example/<script>alert('xss')</script>";
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation(maliciousUrl, out var decision));

        var html = KnowledgeErrorPage.Render(decision);

        Assert.Contains("站点不在允许范围", html, StringComparison.Ordinal);
        Assert.Contains("连接已被 ECL 拒绝", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("location.href", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedirectErrorPageShowsRealStatusOriginalUrlAndRejectedTarget()
    {
        const string requestUrl = "https://zh.minecraft.wiki/wiki/Crafting";
        const string location = "https://evil.example/steal?q=<script>alert(1)</script>";
        var decision = KnowledgeSitePolicy.EvaluateRedirect(302, location, requestUrl);

        var html = KnowledgeErrorPage.Render(decision);

        Assert.Contains("重定向被拒绝", html, StringComparison.Ordinal);
        Assert.Contains("HTTP 302", html, StringComparison.Ordinal);
        Assert.Contains(requestUrl, html, StringComparison.Ordinal);
        Assert.Contains("evil.example", html, StringComparison.Ordinal);
        Assert.Contains("q=", html, StringComparison.Ordinal);
        Assert.Contains("%3Cscript%3E", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemeErrorPageExplainsLocalRejection()
    {
        Assert.False(KnowledgeSitePolicy.TryValidateNavigation("http://zh.minecraft.wiki/", out var decision));

        var html = KnowledgeErrorPage.Render(decision);

        Assert.Contains("协议不受支持", html, StringComparison.Ordinal);
        Assert.Contains("http://zh.minecraft.wiki/", html, StringComparison.Ordinal);
        Assert.Contains("不会自动升级后放行", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorPageLimitsAndEncodesAllDynamicText()
    {
        var huge = "<img src=\"https://evil.example/tracker\">" + new string('a', 10_000);
        var html = KnowledgeErrorPage.Render(new KnowledgeErrorPageRequest
        {
            Error = new EclError(
                KnowledgeErrorCodes.SiteNotAllowed,
                "站点不在允许范围：" + huge,
                "详情：" + huge),
            RequestedUrl = huge,
            RejectedTarget = huge,
        });

        Assert.True(html.Length < 12_000, $"错误页长度应受限制，实际为 {html.Length}。");
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorPageCannotBeRenderedForAllowedDecisions()
    {
        Assert.True(KnowledgeSitePolicy.TryValidateNavigation("https://mcmod.cn/", out var decision));

        Assert.Throws<ArgumentException>(() => KnowledgeErrorPage.Render(decision));
    }

    [Fact]
    public void ErrorCodesAreStable()
    {
        Assert.Equal("knowledge.site_not_allowed", KnowledgeErrorCodes.SiteNotAllowed);
        Assert.Equal("knowledge.scheme_unsupported", KnowledgeErrorCodes.SchemeUnsupported);
        Assert.Equal("knowledge.redirect_blocked", KnowledgeErrorCodes.RedirectBlocked);
        Assert.Equal("knowledge.url_invalid", KnowledgeErrorCodes.UrlInvalid);
    }
}
