// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Core.Logging;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var commandLine = ParsedCommandLine.Parse(args);
        if (commandLine.HasFlag("help") || commandLine.HasFlag("h") || commandLine.Arguments.Count == 0)
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        var paths = AppPaths.Create(commandLine.DataDirectory);
        paths.EnsureCreated();

        var language = commandLine.Language ?? SettingsBootstrap.ReadGlobalString(paths, "launcher.language") ?? CultureInfo.CurrentUICulture.Name;
        var culture = JsonLocalizer.Normalize(CultureInfo.GetCultureInfo(language));

        var services = new ServiceCollection();
        services.AddEclLogging(paths, console: true, consoleMinimumLevel: Serilog.Events.LogEventLevel.Warning);
        services.AddEclCore(paths, culture);
        services.AddEclConfiguration();
        services.AddEclDownload();
        services.AddEclAuth();
        services.AddEclJava();
        services.AddEclMinecraft();

        await using var provider = services.BuildServiceProvider();
        var application = new CliApplication(provider, commandLine);
        return await application.RunAsync().ConfigureAwait(false);
    }

    internal const string HelpText = """
        ECL - Ender Craft Launcher CLI

        用法:
          ecl <命令> [参数] [选项]

        命令:
          version                         显示版本、运行时与数据目录
          instance list [--json]          列出实例
          instance create <名称> --version <版本> [--loader <加载器>] [--loader-version <版本>]
                                          创建实例
          instance delete <实例ID> --force 删除实例
          install <实例ID>                下载/补全原版版本文件
          launch <实例ID> [--dry-run] [--json]
                                          启动实例；--dry-run 仅生成命令
          settings list [--instance <实例ID>] [--json]
                                          列出配置项
          settings get <键> [--instance <实例ID>]
                                          读取配置项
          settings set <键> <值> [--instance <实例ID>]
                                          写入配置项
          settings clear <键> [--instance <实例ID>]
                                          清除配置项
          java list [--json]              列出发现的 Java 运行时
          java install <实例ID>           自动下载该版本需要的 Java 运行时
          account list [--json]           列出账户
          account add-offline <名称> [--default]
                                          添加离线账户
          account remove <账户ID>         删除账户
          account default <账户ID>        设为默认账户

        全局选项:
          --data-dir <目录>               覆盖数据目录
          --lang <zh-CN|en-US>            覆盖界面语言
          --help                          显示帮助

        示例:
          ecl account add-offline Steve --default
          ecl instance create "生存" --version 1.21.1
          ecl install 生存-a1b2c3
          ecl launch 生存-a1b2c3 --dry-run
          ecl settings set java.defaultMemoryMb 8192
        """;
}
