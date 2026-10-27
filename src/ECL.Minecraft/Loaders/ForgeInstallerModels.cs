// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Loaders;

/// <summary>
/// Forge / NeoForge 安装器中的 install_profile.json。
/// spec 0 为旧版（仅需解压 + 下载 universal jar），spec 1+ 需要执行 processors 生成打补丁后的客户端。
/// </summary>
public sealed class ForgeInstallProfile
{
    public int Spec { get; set; }

    public string? Profile { get; set; }

    /// <summary>旧版安装器写入的版本 ID，例如 1.12.2-forge-14.23.5.2860。</summary>
    public string? Version { get; set; }

    /// <summary>旧版安装器要安装的 Forge 坐标，例如 net.minecraftforge:forge:1.12.2-14.23.5.2860。</summary>
    public string? Path { get; set; }

    /// <summary>旧版安装器内 version.json 的路径。</summary>
    public string? Json { get; set; }

    public string? Minecraft { get; set; }

    public string? ServerJarPath { get; set; }

    public string? MirrorList { get; set; }

    /// <summary>旧版安装器直接内嵌的完整版本 JSON。</summary>
    public MinecraftVersion? VersionInfo { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement>? Data { get; set; }

    public List<ForgeProcessor> Processors { get; set; } = [];

    public List<VersionLibrary> Libraries { get; set; } = [];
}

public sealed class ForgeProcessor
{
    /// <summary>为空表示客户端与服务端都需要执行。</summary>
    public List<string>? Sides { get; set; }

    public string? Jar { get; set; }

    public List<string> Classpath { get; set; } = [];

    public List<string> Args { get; set; } = [];
}

public sealed record ForgeInstallerContent(
    MinecraftVersion Version,
    ForgeInstallProfile Profile);

public static class ForgeInstallers
{
    public const string ForgeMavenRoot = "https://maven.minecraftforge.net/";
    public const string NeoForgeMavenRoot = "https://maven.neoforged.net/releases/";

    public static string GetMavenRoot(LoaderKind kind) => kind switch
    {
        LoaderKind.NeoForge => NeoForgeMavenRoot,
        LoaderKind.Forge => ForgeMavenRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "该加载器没有安装器。"),
    };

    public static string GetInstallerUrl(LoaderKind kind, string gameVersion, string loaderVersion)
    {
        var game = gameVersion.Trim();
        var loader = loaderVersion.Trim();

        return kind switch
        {
            LoaderKind.Forge =>
                $"{ForgeMavenRoot}net/minecraftforge/forge/{game}-{loader}/forge-{game}-{loader}-installer.jar",

            // 1.20.1 的 NeoForge 仍发布在 net.neoforged:forge 下。
            LoaderKind.NeoForge when string.Equals(
                game,
                LoaderCatalogParsers.NeoForgeLegacyGameVersion,
                StringComparison.Ordinal) =>
                $"{NeoForgeMavenRoot}net/neoforged/forge/{game}-{loader}/forge-{game}-{loader}-installer.jar",

            LoaderKind.NeoForge =>
                $"{NeoForgeMavenRoot}net/neoforged/neoforge/{loader}/neoforge-{loader}-installer.jar",

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "该加载器没有安装器。"),
        };
    }
}
