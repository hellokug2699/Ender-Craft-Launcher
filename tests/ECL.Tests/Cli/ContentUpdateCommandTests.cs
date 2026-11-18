// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Cli;
using Ecl.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Cli;

/// <summary>
/// F-06 S5 CLI 契约：批量更新只接受稳定数字项目 ID；选择非法时在解析服务之前失败。
/// </summary>
public sealed class ContentUpdateCommandTests
{
    [Fact]
    public async Task ContentUpdateRejectsNonNumericProjectSelection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizer>(new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        await using var provider = services.BuildServiceProvider();
        var application = new CliApplication(
            provider,
            ParsedCommandLine.Parse(["content", "update", "demo", "--project", "jei,abc"]));

        await ConsoleTestGate.Gate.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            exitCode = await application.RunAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleTestGate.Gate.Release();
        }

        Assert.NotEqual(0, exitCode);
        Assert.Contains("只接受数字项目 ID", error.ToString(), StringComparison.Ordinal);
    }
}
