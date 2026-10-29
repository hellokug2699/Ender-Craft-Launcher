// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.Tests.Core;

public sealed class LocalizationTests
{
    [Fact]
    public void ChineseAndEnglishCatalogsHaveTheSameKeys()
    {
        var zh = Load("zh-CN");
        var en = Load("en-US");

        Assert.Equal(en.Keys.OrderBy(key => key, StringComparer.Ordinal),
            zh.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(en.Values, Assert.NotEmpty);
        Assert.All(zh.Values, Assert.NotEmpty);
    }

    private static Dictionary<string, string> Load(string culture)
    {
        var assembly = typeof(JsonLocalizer).Assembly;
        var suffix = $".Locales.{culture}.json";
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
