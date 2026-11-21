// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区首屏轻量化的结构约束（性能交接 §5）：
/// 打开工作区只初始化默认「基本信息」页，其他二级页在首次进入时才加载。
/// 这类约束直接检查页面装配代码，避免将来把重扫描又塞回 InitializeAsync。
/// </summary>
public sealed class InstanceWorkspaceLazyLoadTests
{
    [Fact]
    public void FirstScreenInitializationOnlyLoadsRuntimeAndStorage()
    {
        var source = ReadSource("src/ECL.App/Workspace/ViewModels/InstanceDetailPageViewModel.cs");

        var initialize = Slice(source, "public async Task InitializeAsync()", "private void OnNavigationPropertyChanged");
        Assert.Contains("Runtime.InitializeAsync()", initialize, StringComparison.Ordinal);
        Assert.Contains("Storage.RefreshAsync()", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Mods.InitializeAsync()", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Screenshots.InitializeAsync()", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Schematics.InitializeAsync()", initialize, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondaryPagesAreInitializedOnFirstEntry()
    {
        var source = ReadSource("src/ECL.App/Workspace/ViewModels/InstanceDetailPageViewModel.cs");

        Assert.Contains("EnsurePageInitializedAsync", source, StringComparison.Ordinal);
        Assert.Contains("_initializedPages.Add(page)", source, StringComparison.Ordinal);
        Assert.Contains("case ModsViewModel", source, StringComparison.Ordinal);
        Assert.Contains("case ScreenshotPageViewModel", source, StringComparison.Ordinal);
        Assert.Contains("case SchematicPageViewModel", source, StringComparison.Ordinal);
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"找不到起始片段：{start}");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"找不到结束片段：{end}");
        return text[startIndex..endIndex];
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
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return File.ReadAllText(path);
    }
}
