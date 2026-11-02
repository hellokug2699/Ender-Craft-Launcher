// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Xunit;

namespace Ecl.Tests.Download;

public sealed class DownloadArtifactLifecycleTests
{
    [Fact]
    public async Task ValidPartCanResumeButChangedSizeRestarts()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, new byte[5]);
        var lifecycle = new DownloadArtifactLifecycle();

        var request = Request(destination, size: 10);
        var prepared = await lifecycle.PrepareAsync(part, request);
        Assert.True(prepared.CanResume);
        Assert.Equal(5, prepared.ExistingLength);
        Assert.False(prepared.Restarted);
        Assert.True(File.Exists(DownloadArtifactLifecycle.MetadataPath(part)));

        var changed = await lifecycle.PrepareAsync(part, request with { Size = 4 });
        Assert.True(changed.Restarted);
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(DownloadArtifactLifecycle.MetadataPath(part)));
    }

    [Fact]
    public async Task PartLargerThanExpectedIsDiscarded()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, new byte[11]);
        var lifecycle = new DownloadArtifactLifecycle();

        var prepared = await lifecycle.PrepareAsync(part, Request(destination, size: 10));
        Assert.True(prepared.Restarted);
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task MissingIdentityIsDiscardedButLegacyPartWithSizeIsMigrated()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, new byte[3]);
        var lifecycle = new DownloadArtifactLifecycle();

        var unknown = await lifecycle.PrepareAsync(part, Request(destination, size: null, hash: null));
        Assert.True(unknown.Restarted);
        Assert.False(File.Exists(part));

        await File.WriteAllBytesAsync(part, new byte[3]);
        var migrated = await lifecycle.PrepareAsync(part, Request(destination, size: 10));
        Assert.True(migrated.CanResume);
        var metadata = File.ReadAllText(DownloadArtifactLifecycle.MetadataPath(part));
        Assert.Contains("\"migrated\": true", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupNeverDeletesActivePartAndRemovesStaleOneAfterLease()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, new byte[8]);
        var lifecycle = new DownloadArtifactLifecycle(retention: TimeSpan.Zero);
        await lifecycle.PrepareAsync(part, Request(destination, size: 10));

        var lease = lifecycle.TryAcquire(part);
        Assert.NotNull(lease);
        var active = await lifecycle.CleanupAsync([directory.Path]);
        Assert.Equal(1, active.ActiveSkipped);
        Assert.True(File.Exists(part));

        lease!.Dispose();
        var cleaned = await lifecycle.CleanupAsync([directory.Path]);
        Assert.Equal(1, cleaned.Deleted);
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(DownloadArtifactLifecycle.MetadataPath(part)));
    }

    [Fact]
    public async Task CleanupAppliesTotalCapacityBound()
    {
        using var directory = new TempDirectory();
        var lifecycle = new DownloadArtifactLifecycle(retention: TimeSpan.Zero, maxTotalBytes: 150);
        for (var index = 0; index < 3; index++)
        {
            var destination = Path.Combine(directory.Path, $"file-{index}.bin");
            var part = destination + ".part";
            await File.WriteAllBytesAsync(part, new byte[100]);
            await lifecycle.PrepareAsync(part, Request(destination, size: 100));
        }

        var report = await lifecycle.CleanupAsync([directory.Path]);
        Assert.True(report.Deleted >= 2);
        Assert.True(report.BytesFreed >= 200);
        var remaining = Directory.GetFiles(directory.Path, "*.part");
        Assert.True(remaining.Length <= 1);
    }

    [Fact]
    public async Task CompleteRemovesMetadataAndLeavesNoLock()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "file.bin");
        var part = destination + ".part";
        await File.WriteAllBytesAsync(part, new byte[4]);
        var lifecycle = new DownloadArtifactLifecycle();
        await lifecycle.PrepareAsync(part, Request(destination, size: 4));
        using (var lease = lifecycle.TryAcquire(part))
        {
            Assert.NotNull(lease);
            File.Move(part, destination);
            lifecycle.Complete(part);
        }

        Assert.False(File.Exists(DownloadArtifactLifecycle.MetadataPath(part)));
        Assert.False(File.Exists(part + ".lock"));
        Assert.True(File.Exists(destination));
    }

    private static DownloadRequest Request(string destination, long? size, string? hash = "abc") => new(
        destination,
        [new Uri("https://example.com/file.bin")],
        hash,
        HashingAlgorithm.Sha1,
        size);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-artifact-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
