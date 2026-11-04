// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Core;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateInstallerTests
{
    [Fact]
    public async Task StagesWithoutSwitchingThenAppliesAndRollsBack()
    {
        using var environment = new TempDirectory();
        var layout = new UpdateLayout(environment.Root);
        layout.EnsureCreated();
        var installer = new UpdateInstaller(layout);
        var first = CreatePackage(environment, layout, "1.0.0", "first");
        var second = CreatePackage(environment, layout, "1.0.1", "second");

        var staged = await installer.StageAsync(first.Manifest, first.Archive);
        Assert.True(staged.IsSuccess, staged.Error?.ToString());
        Assert.False(File.Exists(layout.CurrentFile));
        Assert.True(Directory.Exists(layout.GetVersionDirectory(first.Manifest.Version)));

        var applied = await installer.ApplyAsync(first.Manifest, first.Archive);
        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        Assert.Equal("1.0.0", applied.Value!.Version);
        Assert.Null(applied.Value.PreviousVersion);
        Assert.True(File.Exists(layout.CurrentFile));

        var upgraded = await installer.ApplyAsync(second.Manifest, second.Archive);
        Assert.True(upgraded.IsSuccess, upgraded.Error?.ToString());
        Assert.Equal("1.0.1", upgraded.Value!.Version);
        Assert.Equal("1.0.0", upgraded.Value.PreviousVersion);

        var rolledBack = await installer.RollbackAsync();
        Assert.True(rolledBack.IsSuccess, rolledBack.Error?.ToString());
        Assert.Equal("1.0.0", rolledBack.Value!.Version);
        Assert.Equal("1.0.1", rolledBack.Value.PreviousVersion);
    }

    [Fact]
    public async Task RejectsPackageAndFileHashMismatchWithoutSwitching()
    {
        using var environment = new TempDirectory();
        var layout = new UpdateLayout(environment.Root);
        layout.EnsureCreated();
        var installer = new UpdateInstaller(layout);
        var package = CreatePackage(environment, layout, "1.0.0", "payload");

        var wrongPackage = package.Manifest with
        {
            Package = package.Manifest.Package with { Sha256 = new string('0', 64) },
        };
        Assert.Equal(
            "update.package_hash_mismatch",
            (await installer.StageAsync(wrongPackage, package.Archive)).Error?.Code);

        var wrongFile = package.Manifest with
        {
            Files =
            [
                package.Manifest.Files[0] with { Sha256 = new string('1', 64) },
            ],
        };
        Assert.Equal(
            "update.file_hash_mismatch",
            (await installer.StageAsync(wrongFile, package.Archive)).Error?.Code);
        Assert.False(File.Exists(layout.CurrentFile));
    }

    [Fact]
    public async Task RejectsUnexpectedFiles()
    {
        using var environment = new TempDirectory();
        var layout = new UpdateLayout(environment.Root);
        layout.EnsureCreated();
        var installer = new UpdateInstaller(layout);
        var package = CreatePackage(
            environment,
            layout,
            "1.0.0",
            "payload",
            extraFiles: new Dictionary<string, byte[]> { ["extra.dll"] = Encoding.UTF8.GetBytes("extra") });

        var result = await installer.StageAsync(package.Manifest, package.Archive);

        Assert.Equal("update.unexpected_file", result.Error?.Code);
        Assert.False(Directory.Exists(layout.GetVersionDirectory(package.Manifest.Version)));
    }

    [Fact]
    public async Task RefusesAutomaticDowngrade()
    {
        using var environment = new TempDirectory();
        var layout = new UpdateLayout(environment.Root);
        layout.EnsureCreated();
        var installer = new UpdateInstaller(layout);
        var newer = CreatePackage(environment, layout, "1.0.1", "newer");
        var older = CreatePackage(environment, layout, "1.0.0", "older");
        Assert.True((await installer.ApplyAsync(newer.Manifest, newer.Archive)).IsSuccess);

        var result = await installer.ApplyAsync(older.Manifest, older.Archive);

        Assert.Equal("update.downgrade_refused", result.Error?.Code);
        var current = await layout.ReadCurrentAsync();
        Assert.True(current.IsSuccess);
        Assert.Equal("1.0.1", current.Value!.Version);
    }

    private static Package CreatePackage(
        TempDirectory environment,
        UpdateLayout layout,
        string version,
        string content,
        IReadOnlyDictionary<string, byte[]>? extraFiles = null)
    {
        var archive = environment.Combine($"ECL-{version}.zip");
        var entries = new Dictionary<string, byte[]>
        {
            ["ECL.exe"] = Encoding.UTF8.GetBytes(content),
        };
        if (extraFiles is not null)
        {
            foreach (var (name, bytes) in extraFiles)
            {
                entries[name] = bytes;
            }
        }

        DeploymentTestData.CreatePackage(archive, entries);
        var packageBytes = File.ReadAllBytes(archive);
        var manifest = DeploymentTestData.CreateManifest(
            version,
            Path.GetFileName(archive),
            packageBytes.Length,
            UpdateHash.ComputeSha256(packageBytes),
            [new UpdateFile("ECL.exe", Encoding.UTF8.GetByteCount(content), UpdateHash.ComputeSha256(Encoding.UTF8.GetBytes(content)))]);
        return new Package(manifest, archive);
    }

    [Fact]
    public async Task DeploymentNeverTouchesWorkspaceOrUserDataOutsideInstallRoot()
    {
        using var environment = new TempDirectory();
        var layout = new UpdateLayout(environment.Combine("install"));
        layout.EnsureCreated();
        var installer = new UpdateInstaller(layout);
        var workspace = environment.Combine("workspace");
        var instance = Path.Combine(workspace, "instances", "demo-abc123", "minecraft");
        Directory.CreateDirectory(instance);
        await File.WriteAllTextAsync(Path.Combine(instance, "options.txt"), "keep-me");
        Directory.CreateDirectory(Path.Combine(instance, "saves", "world"));
        await File.WriteAllTextAsync(Path.Combine(instance, "saves", "world", "level.dat"), "level");
        var userData = environment.Combine("userdata");
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "accounts.json"), "{}");

        var before = Snapshot(workspace) + Snapshot(userData);

        var first = CreatePackage(environment, layout, "1.0.0", "first");
        var second = CreatePackage(environment, layout, "1.0.1", "second");
        Assert.True((await installer.ApplyAsync(first.Manifest, first.Archive)).IsSuccess);
        Assert.True((await installer.ApplyAsync(second.Manifest, second.Archive)).IsSuccess);
        Assert.True((await installer.RollbackAsync()).IsSuccess);

        Assert.Equal(before, Snapshot(workspace) + Snapshot(userData));
    }

    private static string Snapshot(string root) => string.Join(
        "\n",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}|{UpdateHash.ComputeSha256(File.ReadAllBytes(path))}"));

    private sealed record Package(UpdateManifest Manifest, string Archive);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-deploy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(string name) => Path.Combine(Root, name);

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
