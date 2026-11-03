// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Mods;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>ADR 0012：整合包覆盖文件提取的路径、大小与压缩比安全边界。</summary>
public sealed class ModpackArchiveExtractorTests
{
    [Fact]
    public void ExtractsNormalOverrideIntoGameDirectory()
    {
        using var directory = new TempDirectory();
        var archivePath = Path.Combine(directory.Path, "normal.zip");
        var content = new string('a', 64 * 1024);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "overrides/config/normal.txt", content);
        }

        using var read = ZipFile.OpenRead(archivePath);
        var count = ModpackArchiveExtractor.ExtractOverrides(read, directory.Path, ["overrides"]);

        Assert.Equal(1, count);
        Assert.Equal(
            content,
            File.ReadAllText(Path.Combine(directory.Path, "config", "normal.txt")));
    }

    [Fact]
    public void RejectsHighCompressionRatioEntry()
    {
        using var directory = new TempDirectory();
        var archivePath = Path.Combine(directory.Path, "bomb.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "overrides/config/bomb.bin", new string('\0', 2 * 1024 * 1024));
        }

        using var read = ZipFile.OpenRead(archivePath);
        var exception = Assert.Throws<InvalidDataException>(
            () => ModpackArchiveExtractor.ExtractOverrides(read, directory.Path, ["overrides"]));

        Assert.Contains("压缩比", exception.Message);
        Assert.False(File.Exists(Path.Combine(directory.Path, "config", "bomb.bin")));
    }

    [Fact]
    public void RejectsPathTraversalAndDuplicateDestinations()
    {
        using var directory = new TempDirectory();
        var archivePath = Path.Combine(directory.Path, "traversal.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "overrides/../outside.txt", "x");
        }

        using var read = ZipFile.OpenRead(archivePath);
        Assert.Throws<InvalidDataException>(
            () => ModpackArchiveExtractor.ExtractOverrides(read, directory.Path, ["overrides"]));

        var duplicatePath = Path.Combine(directory.Path, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicatePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "overrides/config/x.txt", "one");
            WriteEntry(archive, "overrides/config/x.txt", "two");
        }

        using var duplicate = ZipFile.OpenRead(duplicatePath);
        Assert.Throws<InvalidDataException>(
            () => ModpackArchiveExtractor.ExtractOverrides(duplicate, directory.Path, ["overrides"]));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ecl-modpack-extractor-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
