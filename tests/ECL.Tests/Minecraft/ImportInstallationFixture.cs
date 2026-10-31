// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 导入实例回归测试使用的安装样本：共享原版、版本隔离的 Fabric/Forge、缺失可选文件，
/// 以及损坏/不完整的版本 JSON。
/// <para>
/// 样本完全由本文件生成，内容固定，因此可以用文件哈希证明“导入不修改原安装”。
/// 版本 JSON 中记录的 SHA-1/大小与实际写出的文件一致，完整样本可以通过启动前的文件校验。
/// </para>
/// </summary>
internal static class ImportInstallationFixture
{
    public const string VanillaVersion = "1.21.1";

    public const string ForgeVanillaVersion = "1.20.1";

    public const string FabricLoaderVersion = "0.16.0";

    public const string ForgeLoaderVersion = "47.4.0";

    public static string FabricProfileId => $"fabric-loader-{FabricLoaderVersion}-{VanillaVersion}";

    public static string ForgeProfileId => $"{ForgeVanillaVersion}-forge-{ForgeLoaderVersion}";

    /// <summary>共享目录样本：原版版本直接放在游戏根目录，没有隔离标记。</summary>
    public static string WriteSharedVanilla(
        TestEnvironment environment,
        InstallFileOptions? options = null,
        string versionId = VanillaVersion)
    {
        WriteVanillaVersion(environment, versionId, options ?? new InstallFileOptions());
        return versionId;
    }

    /// <summary>版本隔离的 Fabric 样本：加载器版本继承原版，用户数据放在版本目录内。</summary>
    public static string WriteIsolatedFabric(
        TestEnvironment environment,
        InstallFileOptions? options = null)
    {
        var files = options ?? new InstallFileOptions();
        WriteVanillaVersion(environment, VanillaVersion, files);

        var profileId = FabricProfileId;
        var versionDirectory = Path.Combine(environment.Paths.VersionsDirectory, profileId);
        Directory.CreateDirectory(versionDirectory);

        Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "config"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "saves"));
        WriteText(Path.Combine(versionDirectory, "options.txt"), "resourcePacks:[\"vanilla\"]\n");
        WriteFabricMod(Path.Combine(versionDirectory, "mods", "examplemod-1.0.jar"), "examplemod", "Example Mod");

        var libraryContent = $"fabric-loader {FabricLoaderVersion}";
        var libraryRelativePath = $"net/fabricmc/fabric-loader/{FabricLoaderVersion}/fabric-loader-{FabricLoaderVersion}.jar";
        var libraryPath = Path.Combine(
            environment.Paths.LibrariesDirectory,
            libraryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (files.Libraries)
        {
            WriteText(libraryPath, libraryContent);
        }

        var library = DescribeContent(libraryContent);
        var manifest = new
        {
            id = profileId,
            inheritsFrom = VanillaVersion,
            type = "release",
            mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            arguments = new
            {
                jvm = new object[] { "-DFabricMcEmu=net.minecraft.client.main.Main" },
                game = new object[] { "--assetIndex", "${assets_index_name}" },
            },
            libraries = new object[]
            {
                new
                {
                    name = $"net.fabricmc:fabric-loader:{FabricLoaderVersion}",
                    url = "https://maven.fabricmc.net/",
                    downloads = new
                    {
                        artifact = new
                        {
                            path = libraryRelativePath,
                            sha1 = library.Sha1,
                            size = library.Size,
                            url = "https://maven.fabricmc.net/net/fabricmc/fabric-loader.jar",
                        },
                    },
                },
            },
        };

        WriteText(Path.Combine(versionDirectory, $"{profileId}.json"), JsonSerializer.Serialize(manifest));
        return profileId;
    }

    /// <summary>版本隔离的 Forge 样本：合并后的版本 JSON 通过 --fml.mcVersion 记录原版版本。</summary>
    public static string WriteIsolatedForge(
        TestEnvironment environment,
        InstallFileOptions? options = null)
    {
        var files = options ?? new InstallFileOptions();
        WriteVanillaVersion(environment, ForgeVanillaVersion, files);

        var profileId = ForgeProfileId;
        var versionDirectory = Path.Combine(environment.Paths.VersionsDirectory, profileId);
        Directory.CreateDirectory(versionDirectory);

        Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "config"));
        WriteText(Path.Combine(versionDirectory, "options.txt"), "resourcePacks:[\"vanilla\"]\n");
        WriteForgeMod(Path.Combine(versionDirectory, "mods", "exampleforge-1.0.jar"));

        var libraryContent = $"forge {ForgeLoaderVersion}";
        var libraryRelativePath =
            $"net/minecraftforge/forge/{ForgeVanillaVersion}-{ForgeLoaderVersion}/forge-{ForgeVanillaVersion}-{ForgeLoaderVersion}-universal.jar";
        var libraryPath = Path.Combine(
            environment.Paths.LibrariesDirectory,
            libraryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (files.Libraries)
        {
            WriteText(libraryPath, libraryContent);
        }

        var library = DescribeContent(libraryContent);
        var manifest = new
        {
            id = profileId,
            inheritsFrom = ForgeVanillaVersion,
            type = "release",
            mainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            arguments = new
            {
                game = new object[]
                {
                    "--launchTarget",
                    "forgeclient",
                    "--fml.forgeVersion",
                    ForgeLoaderVersion,
                    "--fml.mcVersion",
                    ForgeVanillaVersion,
                },
                jvm = new object[]
                {
                    "-DlibraryDirectory=${library_directory}",
                    "-p",
                    "${library_directory}/cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar",
                },
            },
            libraries = new object[]
            {
                new
                {
                    name = $"net.minecraftforge:forge:{ForgeVanillaVersion}-{ForgeLoaderVersion}",
                    url = "https://maven.minecraftforge.net/",
                    downloads = new
                    {
                        artifact = new
                        {
                            path = libraryRelativePath,
                            sha1 = library.Sha1,
                            size = library.Size,
                            url = "https://maven.minecraftforge.net/forge-universal.jar",
                        },
                    },
                },
            },
        };

        WriteText(Path.Combine(versionDirectory, $"{profileId}.json"), JsonSerializer.Serialize(manifest));
        return profileId;
    }

    /// <summary>版本 JSON 无法解析（其它启动器写入中断）。</summary>
    public static void WriteCorruptVersion(TestEnvironment environment, string versionId)
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, $"{versionId}.json"), $$"""{"id": "{{versionId}}", """);
    }

    /// <summary>版本 JSON 可解析，但没有 mainClass，也没有可继承的父版本。</summary>
    public static void WriteIncompleteVersion(TestEnvironment environment, string versionId)
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, $"{versionId}.json"), $$"""
            {
              "id": "{{versionId}}",
              "type": "release"
            }
            """);
    }

    /// <summary>继承链指向不存在的父版本（不完整的安装，或父版本已被外部启动器删除）。</summary>
    public static void WriteOrphanVersion(TestEnvironment environment, string versionId, string missingParent)
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, $"{versionId}.json"), $$"""
            {
              "id": "{{versionId}}",
              "inheritsFrom": "{{missingParent}}",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient"
            }
            """);
    }

    /// <summary>没有同名版本 JSON 的目录（例如 versions/assets），扫描时必须忽略。</summary>
    public static void WriteDirectoryWithoutManifest(TestEnvironment environment, string name)
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, name);
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, "keep.txt"), "not a version");
    }

    public static string WriteResourcePack(TestEnvironment environment, string fileName, string description)
    {
        var path = Path.Combine(environment.Root, "samples", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "pack.mcmeta",
            JsonSerializer.Serialize(new { pack = new { pack_format = 15, description } }));
        return path;
    }

    public static string WriteShaderPack(TestEnvironment environment, string fileName)
    {
        var path = Path.Combine(environment.Root, "samples", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "shaders/final.fsh", "void main() {}");
        return path;
    }

    /// <summary>写出一份崩溃报告与 latest.log，内容包含可被崩溃分析识别的字段。</summary>
    public static void WriteCrashReport(string gameDirectory, string fileName = "crash-2026-01-02_03.04.05-client.txt")
    {
        var directory = Path.Combine(gameDirectory, "crash-reports");
        Directory.CreateDirectory(directory);
        WriteText(Path.Combine(directory, fileName), """
            ---- Minecraft Crash Report ----
            Time: 2026-01-02 03:04:05
            Description: Rendering overlay

            java.lang.RuntimeException: examplemod blew up
            	at examplemod.Main.tick(Main.java:1)

            Minecraft Version: 1.21.1
            Java Version: 21.0.1

            A detailed walkthrough of the error
            """);

        var logDirectory = Path.Combine(gameDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        WriteText(Path.Combine(logDirectory, "latest.log"), "Minecraft Crash Report\nException in examplemod\n");
    }

    private static void WriteVanillaVersion(
        TestEnvironment environment,
        string versionId,
        InstallFileOptions options)
    {
        var paths = environment.Paths;
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(versionDirectory);

        var clientJarContent = $"client jar for {versionId}";
        var clientJarPath = Path.Combine(versionDirectory, $"{versionId}.jar");
        if (options.ClientJar)
        {
            WriteText(clientJarPath, clientJarContent);
        }

        var clientJar = DescribeContent(clientJarContent);

        var libraryContent = $"demo library for {versionId}";
        var libraryRelativePath = "com/example/demo/1.0/demo-1.0.jar";
        var libraryPath = Path.Combine(
            paths.LibrariesDirectory,
            libraryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (options.Libraries)
        {
            WriteText(libraryPath, libraryContent);
        }

        var library = DescribeContent(libraryContent);

        var objectContent = $"asset object for {versionId}";
        var objectHash = Sha1(objectContent);
        var objectPath = Path.Combine(paths.AssetsDirectory, "objects", objectHash[..2], objectHash);
        if (options.AssetObjects)
        {
            WriteText(objectPath, objectContent);
        }

        var assetIndexJson = JsonSerializer.Serialize(new
        {
            objects = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["minecraft/lang/en_us.json"] = new
                {
                    hash = objectHash,
                    size = Encoding.UTF8.GetByteCount(objectContent),
                },
            },
        });
        var assetIndexPath = Path.Combine(paths.AssetsDirectory, "indexes", $"{versionId}.json");
        if (options.AssetIndex)
        {
            WriteText(assetIndexPath, assetIndexJson);
        }

        var assetIndex = DescribeContent(assetIndexJson);

        var loggingContent = "<Configuration/>";
        var loggingId = $"client-{versionId}.xml";
        var loggingPath = Path.Combine(paths.AssetsDirectory, "logging", loggingId);
        if (options.LoggingConfig)
        {
            WriteText(loggingPath, loggingContent);
        }

        var logging = DescribeContent(loggingContent);

        var manifest = new
        {
            id = versionId,
            type = "release",
            mainClass = "net.minecraft.client.main.Main",
            assets = versionId,
            assetIndex = new
            {
                id = versionId,
                sha1 = assetIndex.Sha1,
                size = assetIndex.Size,
                url = "https://example.invalid/assets.json",
            },
            downloads = new
            {
                client = new
                {
                    sha1 = clientJar.Sha1,
                    size = clientJar.Size,
                    url = "https://example.invalid/client.jar",
                },
            },
            logging = new
            {
                client = new
                {
                    argument = "-Dlog4j.configurationFile=${path}",
                    file = new
                    {
                        id = loggingId,
                        sha1 = logging.Sha1,
                        size = logging.Size,
                        url = "https://example.invalid/log4j.xml",
                    },
                    type = "log4j2-xml",
                },
            },
            libraries = new object[]
            {
                new
                {
                    name = "com.example:demo:1.0",
                    downloads = new
                    {
                        artifact = new
                        {
                            path = libraryRelativePath,
                            sha1 = library.Sha1,
                            size = library.Size,
                            url = "https://example.invalid/demo.jar",
                        },
                    },
                },
            },
            arguments = new
            {
                jvm = new object[] { "-Djava.library.path=${natives_directory}" },
                game = new object[]
                {
                    "--username",
                    "${auth_player_name}",
                    "--version",
                    "${version_name}",
                    "--gameDir",
                    "${game_directory}",
                    "--assetsDir",
                    "${assets_root}",
                    "--assetIndex",
                    "${assets_index_name}",
                    "--uuid",
                    "${auth_uuid}",
                    "--accessToken",
                    "${auth_access_token}",
                    "--userType",
                    "${user_type}",
                },
            },
        };

        WriteText(Path.Combine(versionDirectory, $"{versionId}.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>写一个只含最小 Fabric 元数据的 mod jar。</summary>
    public static void WriteFabricMod(string path, string modId, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "fabric.mod.json", $$"""
            {
              "id": "{{modId}}",
              "name": "{{name}}",
              "version": "1.0.0",
              "depends": { "fabricloader": ">=0.16.0", "minecraft": "1.21.1" }
            }
            """);
        WriteEntry(archive, "examplemod/Example.class", "not a real class");
    }

    private static void WriteForgeMod(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "META-INF/mods.toml", """
            modLoader="javafml"
            loaderVersion="[47,)"

            [[mods]]
            modId="exampleforge"
            version="1.0.0"
            displayName="Example Forge Mod"

            [[dependencies.exampleforge]]
            modId="forge"
            mandatory=true
            versionRange="[47,)"
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static (string Sha1, long Size) DescribeContent(string content)
    {
        // 即使可选文件没有写出，版本 JSON 仍然记录期望值：缺失文件会在启动检查时被报告。
        var bytes = Encoding.UTF8.GetBytes(content);
        return (Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), bytes.Length);
    }

    private static string Sha1(string content) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>安装样本中可以选择缺失的文件，用来模拟“缺失可选文件”的安装。</summary>
internal sealed record InstallFileOptions
{
    public bool ClientJar { get; init; } = true;

    public bool Libraries { get; init; } = true;

    public bool AssetIndex { get; init; } = true;

    public bool AssetObjects { get; init; } = true;

    public bool LoggingConfig { get; init; } = true;
}

/// <summary>文件树快照，用于断言导入/扫描没有改动原安装。</summary>
internal sealed class FileTreeSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _hashes;

    private FileTreeSnapshot(IReadOnlyDictionary<string, string> hashes) => _hashes = hashes;

    public IReadOnlyDictionary<string, string> Files => _hashes;

    public static FileTreeSnapshot Capture(string root, Func<string, bool>? include = null)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (include is not null && !include(relative))
                {
                    continue;
                }

                using var stream = File.OpenRead(path);
                hashes[relative] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }

        return new FileTreeSnapshot(hashes);
    }

    /// <summary>返回新增、删除、内容变化的文件描述；完全一致时为空。</summary>
    public IReadOnlyList<string> Diff(FileTreeSnapshot other)
    {
        var differences = new List<string>();

        foreach (var pair in _hashes)
        {
            if (!other._hashes.TryGetValue(pair.Key, out var hash))
            {
                differences.Add($"删除：{pair.Key}");
            }
            else if (!string.Equals(hash, pair.Value, StringComparison.Ordinal))
            {
                differences.Add($"修改：{pair.Key}");
            }
        }

        foreach (var path in other._hashes.Keys)
        {
            if (!_hashes.ContainsKey(path))
            {
                differences.Add($"新增：{path}");
            }
        }

        return differences;
    }
}

/// <summary>共享游戏根目录里真正属于游戏安装的文件；排除 ECL 自己的 instances/config 等数据目录。</summary>
internal static class GameDataFiles
{
    public static bool IsGameData(string relativePath) =>
        relativePath.StartsWith("versions/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
        !relativePath.Contains('/');
}
