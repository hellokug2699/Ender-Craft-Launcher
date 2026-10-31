// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class SafeZipExtractorTests
{
    [Fact]
    public async Task ExtractsSafeEntries()
    {
        using var environment = new TempDirectory();
        var archive = environment.Combine("package.zip");
        CreateArchive(archive, ("ECL.exe", Encoding.UTF8.GetBytes("app")), ("mods/a.jar", Encoding.UTF8.GetBytes("mod")));

        var result = await SafeZipExtractor.ExtractAsync(archive, environment.Combine("out"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("app", await File.ReadAllTextAsync(environment.Combine("out", "ECL.exe")));
        Assert.Equal("mod", await File.ReadAllTextAsync(environment.Combine("out", "mods", "a.jar")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("mods/../../evil.txt")]
    public async Task RejectsPathTraversalAndAbsoluteEntries(string entryName)
    {
        using var environment = new TempDirectory();
        var archive = environment.Combine("package.zip");
        CreateArchive(archive, (entryName, Encoding.UTF8.GetBytes("evil")));

        var result = await SafeZipExtractor.ExtractAsync(archive, environment.Combine("out"));

        Assert.False(result.IsSuccess);
        Assert.Contains("update.archive_", result.Error!.Code, StringComparison.Ordinal);
        Assert.False(File.Exists(environment.Combine("evil.txt")));
    }

    [Fact]
    public async Task RejectsDuplicateEntriesAndSymlinks()
    {
        using var environment = new TempDirectory();
        var duplicate = environment.Combine("duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            using (var first = archive.CreateEntry("a.txt").Open())
            {
                first.Write(Encoding.UTF8.GetBytes("one"));
            }

            using (var second = archive.CreateEntry("A.TXT").Open())
            {
                second.Write(Encoding.UTF8.GetBytes("two"));
            }
        }

        var duplicateResult = await SafeZipExtractor.ExtractAsync(duplicate, environment.Combine("out1"));
        Assert.Equal("update.archive_duplicate_path", duplicateResult.Error?.Code);

        var symlink = environment.Combine("symlink.zip");
        using (var archive = ZipFile.Open(symlink, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = 0xA000 << 16;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes("target"));
        }

        var symlinkResult = await SafeZipExtractor.ExtractAsync(symlink, environment.Combine("out2"));
        Assert.Equal("update.archive_symlink", symlinkResult.Error?.Code);
    }

    [Fact]
    public async Task EnforcesSizeAndEntryLimits()
    {
        using var environment = new TempDirectory();
        var archive = environment.Combine("large.zip");
        CreateArchive(archive, ("a.bin", new byte[8]));

        var fileLimit = await SafeZipExtractor.ExtractAsync(
            archive,
            environment.Combine("out1"),
            new ArchiveLimits(MaxFileBytes: 4));
        Assert.Equal("update.archive_file_too_large", fileLimit.Error?.Code);

        var entryLimit = await SafeZipExtractor.ExtractAsync(
            archive,
            environment.Combine("out2"),
            new ArchiveLimits(MaxEntries: 0));
        Assert.Equal("update.archive_too_many_entries", entryLimit.Error?.Code);
    }

    private static void CreateArchive(string path, params (string Name, byte[] Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(content);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-deploy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不影响结果。
            }
        }
    }
}
