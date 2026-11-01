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
        Ecl.Core.Storage.WorkspaceBootstrap.Apply(
            paths,
            SettingsBootstrap.ReadGlobalString(paths, "launcher.minecraftDirectory"));
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
          update check [--feed <URL>] [--trust <文件>] [--install-root <目录>] [--json]
                                          按 launcher.checkUpdates / launcher.updateChannel 检查更新
          instance list [--json]          列出实例
          workspace status [--json]       查看工作区配置、可用性与存储边界
          workspace set <目录>            确认 Minecraft 工作区（实例与游戏文件所在位置）
          workspace validate [--json]     校验当前工作区是否仍可用
          workspace suggest [--json]      推荐可见的数据盘目录
          workspace adopt-legacy          继续使用旧版数据目录（仅在确有旧数据时可用）
          workspace skip                  暂不配置；创建/下载前仍会要求确认
          workspace roots [--json]        列出已登记的工作区根目录
          workspace migrate --to <目录> [--apply] [--json]
                                          预览/执行工作区迁移：复制→校验→切换，源目录保留
          workspace migrate --resume|--discard|--status
                                          恢复、放弃或查看中断的迁移
          instance create <名称> --version <版本> [--loader <加载器>] [--loader-version <版本>]
                           [--fabric-api-version <版本>] [--optifine-version <type:patch>]
                                          创建实例；加载器支持 vanilla、fabric、forge、neoforge、cleanroom
          instance delete <实例ID> [--force] [--keep-data] [--dry-run] [--json]
                                          预览删除计划；--force 执行，--keep-data 只移除记录
          instance scan [--json]          扫描 versions 目录中已有的版本
          instance import <版本ID> [--name <名称>]
                                          把已有版本导入为实例（引用原目录，不复制）
          loader list --version <版本> [--type fabric|forge|neoforge|cleanroom|fabric-api|optifine] [--json]
                                          列出加载器/组件版本与更新时间
          install <实例ID>                下载/补全版本文件、加载器与组件
          launch <实例ID> [--dry-run] [--accept-eula] [--accept-java-risk] [--json]
                                          启动实例；--dry-run 仅生成命令，
                                          --accept-eula 首次启动时同意 EULA，
                                          --accept-java-risk 显式接受实例固定 Java 的已知风险
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
          mod list <实例ID> [--json]      列出 Mod、启用状态与问题
          mod check <实例ID> [--json]     检查依赖/冲突/重复/加载器不匹配
          mod enable|disable <实例ID> <文件>
                                          启用/禁用单个 Mod
          mod enable-all|disable-all <实例ID>
                                          批量启用/禁用
          mod import <实例ID> <文件...>   导入 .jar 到实例 mods/
          mod delete <实例ID> <文件>      删除 Mod
          modpack import <实例ID> <整合包> 导入 Modrinth .mrpack 或 CurseForge .zip
          modpack export <实例ID> <输出>  导出为 Modrinth .mrpack
          curseforge status [--check] [--json]
                                          查看 CurseForge 代理状态，--check 验证连通性
          curseforge search <关键词> [--game-version <版本>] [--loader fabric|forge|neoforge|quilt]
                           [--page-size <1-50>] [--json]
                                          搜索 CurseForge Mod
          curseforge files <项目ID|slug> [--game-version <版本>] [--loader <加载器>] [--json]
                                          列出项目的文件，用于选择 --file-id
          curseforge install <实例ID> <项目ID|slug|链接> [--file-id <ID>] [--no-dependencies] [--json]
                                          下载并安装 CurseForge Mod（默认同时安装必需依赖）
          pack list <实例ID> [--type resource|shader] [--json]
                                          列出资源包/光影包及启用状态
          pack enable|disable <实例ID> <文件> [--type resource|shader]
                                          启用/禁用资源包或光影包（写入 options.txt）
          pack import <实例ID> <文件> [--type resource|shader]
                                          导入资源包/光影包
          pack delete <实例ID> <文件> [--type resource|shader]
                                          删除资源包/光影包
          crash <实例ID> [--json]         分析最近一次崩溃与可疑 Mod
          diagnostics export <实例ID> [--output <路径>] [--json]
                                          导出脱敏诊断包
          account list [--json]           列出账户
          account add-offline <名称> [--default]
                                          添加离线账户
          account login-microsoft [--default]
                                          打开浏览器完成 Microsoft 登录（实机验证用）
          account refresh [账户ID]        静默刷新 Microsoft 凭据（不打开浏览器）
          account login-thirdparty <服务器ID|地址> --username <用户名> [--default]
                                          使用第三方认证服务器登录（密码不回显）
          authserver list [--json]        列出已添加的认证服务器
          authserver add <地址> [--register <注册链接>]
                                          添加认证服务器并校验元数据
          authserver update <ID> [--url <新地址>] [--register <注册链接>]
                                          修改认证服务器地址/注册链接
          authserver remove <ID>          删除认证服务器
          account remove <账户ID>         删除账户
          account default <账户ID>        设为默认账户

        全局选项:
          --data-dir <目录>               覆盖数据目录
          --lang <zh-CN|en-US>            覆盖界面语言
          --help                          显示帮助

        示例:
          ecl workspace set "<可见数据盘目录，例如 <盘符>:\Games\ECL>"
          ecl account add-offline Steve --default
          ecl instance create "Survival" --version 1.21.1
          ecl install survival-a1b2c3
          ecl launch survival-a1b2c3 --dry-run
          ecl mod list survival-a1b2c3
          ecl modpack import survival-a1b2c3 "C:\packs\example.mrpack"
          ecl pack enable survival-a1b2c3 "example.zip"
          ecl settings set java.jvmPreset aikar
          ecl settings set java.runtimeProvider adoptium
          ecl settings set java.defaultMemoryMb 8192
        """;
}
