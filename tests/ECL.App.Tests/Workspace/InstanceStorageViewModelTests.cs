// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Storage;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 基本信息存储卡（B2 步骤 3b-2）：未知/不可访问不显示为 0；
/// 状态来自存储分析的同一语义；管理入口只把实例 ID 交给 Shell。
/// </summary>
public sealed class InstanceStorageViewModelTests
{
    private static readonly GameInstance Instance = new()
    {
        Id = "instance-a",
        Name = "实例 A",
        GameVersion = "1.21.1",
        VersionId = "1.21.1-fabric",
        Loader = "fabric",
        LoaderVersion = "0.16.10",
    };

    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    private static StorageUsage Usage(
        long? bytes,
        int files,
        StorageUsageStatus status) => new(
        "instance:instance-a",
        "实例 A",
        @"C:\instances\instance-a\minecraft",
        bytes,
        files,
        status,
        null,
        IsCleanable: false,
        Error: null);

    private static InstanceStorageViewModel Create(
        Func<GameInstance, CancellationToken, Task<StorageUsage>> measure,
        Action<string>? manage = null) =>
        new(Instance, measure, manage, Localizer());

    [Fact]
    public async Task ScannedUsageShowsSizeAndFileCount()
    {
        using var storage = Create((_, _) => Task.FromResult(Usage(
            5 * 1024 * 1024,
            12,
            StorageUsageStatus.Scanned)));

        await storage.RefreshAsync();

        Assert.True(storage.HasKnownSize);
        Assert.Equal("5 MB", storage.SizeText);
        Assert.Equal("已统计当前实例可识别文件", storage.StatusText);
        Assert.Contains("12 个可识别文件", storage.DetailText);
        Assert.False(storage.IsScanning);
    }

    [Fact]
    public async Task EstimatedUsageIsLabelledAsEstimate()
    {
        using var storage = Create((_, _) => Task.FromResult(Usage(
            1024,
            3,
            StorageUsageStatus.Estimated)));

        await storage.RefreshAsync();

        Assert.True(storage.HasKnownSize);
        Assert.Equal("1 KB", storage.SizeText);
        Assert.Contains("估算", storage.StatusText);
    }

    [Fact]
    public async Task UnavailableUsageNeverShowsZeroBytes()
    {
        using var storage = Create((_, _) => Task.FromResult(Usage(
            bytes: null,
            files: 0,
            StorageUsageStatus.Unavailable)));

        await storage.RefreshAsync();

        Assert.False(storage.HasKnownSize);
        Assert.Equal("不可用", storage.SizeText);
        Assert.DoesNotContain("0 B", storage.SizeText);
        Assert.Contains("不计为 0", storage.DetailText);
    }

    [Fact]
    public async Task MeasurementFailureIsReportedWithoutThrowing()
    {
        using var storage = Create((_, _) => throw new IOException("boom"));

        await storage.RefreshAsync();

        Assert.False(storage.HasKnownSize);
        Assert.Equal("统计实例存储失败", storage.StatusText);
        Assert.Equal("不可用", storage.SizeText);
        Assert.False(storage.IsScanning);
    }

    [Fact]
    public void ManageStoragePassesTheStableInstanceId()
    {
        string? requested = null;
        using var storage = Create(
            (_, _) => Task.FromResult(Usage(0, 0, StorageUsageStatus.Scanned)),
            instanceId => requested = instanceId);

        storage.ManageStorageCommand.Execute(null);

        Assert.Equal("instance-a", requested);
    }

    [Fact]
    public async Task RefreshAfterDisposeDoesNothing()
    {
        var storage = Create((_, _) => Task.FromResult(Usage(1024, 1, StorageUsageStatus.Scanned)));
        storage.Dispose();

        await storage.RefreshAsync();

        Assert.True(storage.IsDisposed);
        Assert.False(storage.HasKnownSize);
        Assert.Equal("尚未统计实例占用", storage.StatusText);
    }
}
