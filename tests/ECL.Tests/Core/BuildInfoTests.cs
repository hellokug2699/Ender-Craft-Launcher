// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Xunit;

namespace Ecl.Tests.Core;

public sealed class BuildInfoTests
{
    [Fact]
    public void BuildTimeIsEmbeddedAsAssemblyMetadata()
    {
        // Directory.Build.props 会把构建时间写入程序集；缺失时界面就无法显示来源版本。
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.BuildTime));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", BuildInfo.BuildTime);
    }
}
