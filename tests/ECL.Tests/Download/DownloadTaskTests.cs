// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Xunit;

namespace Ecl.Tests.Download;

public sealed class DownloadTaskTests
{
    [Fact]
    public void Progress_UsesDownloadedAndTotalBytes()
    {
        var task = new DownloadTask(
            Guid.NewGuid(),
            "test.jar",
            DownloadTaskState.Downloading,
            DownloadedBytes: 25,
            TotalBytes: 100,
            BytesPerSecond: 10,
            Source: "Modrinth",
            Destination: "C:/Downloads/test.jar",
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.Equal(0.25, task.Progress);
    }

    [Fact]
    public void Progress_IsNull_WhenTotalSizeIsUnknown()
    {
        var task = new DownloadTask(
            Guid.NewGuid(),
            "test.jar",
            DownloadTaskState.Downloading,
            DownloadedBytes: 25,
            TotalBytes: null,
            BytesPerSecond: null,
            Source: null,
            Destination: null,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.Null(task.Progress);
    }
}
