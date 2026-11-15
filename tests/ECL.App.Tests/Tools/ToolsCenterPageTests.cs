// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Tools;

/// <summary>
/// 工具中心（迁移规划 §6.2 #12）：入口清单只读真实状态、工具内容由条目自己的对象承载、
/// 扩展区域只陈述本机安装记录，状态损坏时报告错误而不是伪装成“没有扩展”。
/// </summary>
public sealed class ToolsCenterPageTests
{
    [Fact]
    public async Task ActivatingThePageReadsOnlyLocalAdditionState()
    {
        var host = new Host();

        await host.Page.ActivateAsync();

        // 进入工具中心不请求官方目录、不发起网络；目录检查仍属于设置 → 扩展管理。
        Assert.Equal(0, host.Service.CatalogCalls);
        Assert.Empty(host.Page.InstalledAdditions);
        Assert.False(host.Page.HasAdditionsError);
        Assert.True(host.Page.ShowAdditionsEmpty);
        Assert.Contains("未读取到任何已安装扩展", host.Page.AdditionsStatus);
    }

    [Fact]
    public async Task InstalledAdditionsAreListedWithTheirRealStatus()
    {
        var host = new Host();
        host.Service.Installed.Add(Installed("ecl.alpha", "1.2.0"));
        host.Service.Installed.Add(Installed("ecl.beta", "2.0.0", enabled: false, type: AdditionType.Data));

        await host.Page.ActivateAsync();

        Assert.Equal(2, host.Page.InstalledAdditions.Count);
        var alpha = host.Page.InstalledAdditions[0];
        Assert.Equal("ecl.alpha", alpha.Id);
        Assert.Equal("1.2.0", alpha.VersionText);
        Assert.Equal("功能扩展", alpha.TypeText);
        Assert.Equal("已启用", alpha.StatusText);
        Assert.Contains("Addition Runtime", alpha.Note);

        var beta = host.Page.InstalledAdditions[1];
        Assert.Equal("数据扩展", beta.TypeText);
        Assert.Equal("已禁用", beta.StatusText);
        Assert.True(host.Page.HasInstalledAdditions);
        Assert.False(host.Page.ShowAdditionsEmpty);
        Assert.Contains("已安装 2 个扩展", host.Page.AdditionsStatus);
    }

    [Fact]
    public async Task CorruptAdditionRecordsAreReportedInsteadOfAnEmptyState()
    {
        var host = new Host();
        host.Service.Corrupt = true;
        host.Service.CorruptError = "installed.json 损坏";

        await host.Page.ActivateAsync();

        Assert.True(host.Page.HasAdditionsError);
        Assert.False(host.Page.ShowAdditionsEmpty);
        Assert.Contains("installed.json 损坏", host.Page.AdditionsErrorText);
        Assert.Contains("不可读", host.Page.AdditionsStatus);
    }

    [Fact]
    public async Task OpeningAToolUsesTheEntryContentAndBackReturnsToTheList()
    {
        var activated = 0;
        var content = new object();
        var tool = new ToolsCenterTool(
            "sample",
            "Sample tool",
            "Purpose",
            "可用",
            "note",
            content,
            () =>
            {
                activated++;
                return Task.CompletedTask;
            });
        var host = new Host(tool);

        await host.Page.OpenToolCommand.ExecuteAsync(tool);

        Assert.Equal(1, activated);
        Assert.Same(tool, host.Page.SelectedTool);
        Assert.True(host.Page.HasSelectedTool);
        Assert.Same(content, host.Page.SelectedToolContent);

        host.Page.BackToToolsCommand.Execute(null);

        Assert.False(host.Page.HasSelectedTool);
        Assert.Null(host.Page.SelectedToolContent);
    }

    [Fact]
    public void ManageAdditionsUsesTheShellProvidedEntry()
    {
        var host = new Host();
        var invoked = 0;
        host.Page.OpenAdditionsManagement = () => invoked++;

        host.Page.ManageAdditionsCommand.Execute(null);

        Assert.Equal(1, invoked);
    }

    [Fact]
    public void TheViewIsAnEntryListWithOneSharedContentHost()
    {
        var xaml = ReadSource("src/ECL.App/Views/ToolsView.xaml");

        // 工具入口卡与扩展行各只有一个模板；工具内容通过 ContentControl 交给条目自己的对象。
        Assert.Contains("x:Key=\"ToolCardTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AdditionRowTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedToolContent", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenToolCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("BackToToolsCommand", xaml, StringComparison.Ordinal);
        // 不再是旧的大型多 Tab 工具箱容器。
        Assert.DoesNotContain("<TabControl", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("知识浏览", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("世界工具", xaml, StringComparison.Ordinal);
    }

    private static InstalledAddition Installed(
        string id,
        string version,
        bool enabled = true,
        AdditionType type = AdditionType.Feature) =>
        new(
            AdditionId.Parse(id),
            type,
            Ecl.Deployment.ReleaseVersion.Parse(version),
            Path.Combine(Path.GetTempPath(), "ecl-tools-tests", id, version),
            "Ecl.TestAddition.dll",
            enabled,
            DataSchemaVersion: 1,
            Files: [],
            DateTimeOffset.Parse("2026-11-01T10:00:00Z", CultureInfo.InvariantCulture));

    private sealed class FakeCatalog(params ToolsCenterTool[] tools) : IToolsCenterCatalog
    {
        public IReadOnlyList<ToolsCenterTool> GetTools() => tools;
    }

    private sealed class Host
    {
        public Host(params ToolsCenterTool[] tools)
        {
            Service = new FakeAdditionsService();
            ToolsCenterTool[] entries = tools.Length == 0
                ?
                [
                    new ToolsCenterTool("engine", "引擎", "用途", "可用", null, new object()),
                ]
                : tools;
            Page = new ToolsViewModel(
                Service,
                new FakeCatalog(entries),
                new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        }

        public FakeAdditionsService Service { get; }

        public ToolsViewModel Page { get; }
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
