// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

using Xunit;

namespace Ecl.App.Tests.Home;

/// <summary>
/// 主页页面结构回归（home.md §2、§6）：启动卡双区、中央实例切换栏、下方三块必须存在；
/// 主页不再包含旧的 Java 区域、免责声明小字与重复入口；实例工作区不再复制全局账号区块。
/// 说明：这是对 XAML 的结构断言，针对的是“重复入口/旧区域”这类不会通过 ViewModel 暴露的问题。
/// </summary>
public sealed class HomePageStructureTests
{
    [Fact]
    public void HomeViewContainsTheApprovedRegions()
    {
        var xaml = ReadSource("src/ECL.App/Views/HomeView.xaml");

        // 启动卡：左侧启动与总进度、右侧普通/高级、错误日志入口。
        Assert.Contains("Launch.LaunchCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Launch.TotalProgressValue", xaml, StringComparison.Ordinal);
        Assert.Contains("Launch.Steps", xaml, StringComparison.Ordinal);
        Assert.Contains("Launch.LogLines", xaml, StringComparison.Ordinal);
        Assert.Contains("Launch.OpenErrorLogCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Launch.PreviewCommand", xaml, StringComparison.Ordinal);

        // 中央实例切换栏：折叠四格、展开布局、编辑/删除模式、加载器色条。
        Assert.Contains("Switcher.Collapsed", xaml, StringComparison.Ordinal);
        Assert.Contains("Switcher.Layout", xaml, StringComparison.Ordinal);
        Assert.Contains("Switcher.ToggleEditModeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Switcher.ToggleDeleteModeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LoaderKindBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowDrop=\"True\"", xaml, StringComparison.Ordinal);

        // 下方三个等高区域：账号、实例工作区/下载中心入口、Multiplayer。
        Assert.Contains("MultiplayerTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenWorkspaceCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenDownloadsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ToggleAccountMenuCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"200\"", xaml, StringComparison.Ordinal);
        // 窄窗口（1080×720 + 侧栏 + Inspector）必须有紧凑布局，而不是把文字裁切到不可读。
        Assert.Contains("IsCompactLayout", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeViewNoLongerContainsRemovedDuplicateBlocks()
    {
        var xaml = ReadSource("src/ECL.App/Views/HomeView.xaml");

        // home.md §6：主页右侧 Java 版本区域、免责声明式小字与重复入口必须删除。
        Assert.DoesNotContain("运行环境", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.JavaStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("快速入口", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("最近访问", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("最近一次启动", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("免责", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("home.footer", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceWorkspaceNoLongerDuplicatesTheGlobalAccountList()
    {
        var xaml = ReadSource("src/ECL.App/Views/InstanceBasicInfoView.xaml");

        Assert.DoesNotContain("Instances.Accounts", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("instances.accountTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SignInMicrosoftCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AddThirdPartyAccountCommand", xaml, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到页面源文件：{path}");
        return File.ReadAllText(path);
    }
}

