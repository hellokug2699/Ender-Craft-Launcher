// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Loaders;

public enum LoaderKind
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    Cleanroom,
}

public static class LoaderKinds
{
    public const string Vanilla = "vanilla";
    public const string Fabric = "fabric";
    public const string Forge = "forge";
    public const string NeoForge = "neoforge";
    public const string Cleanroom = "cleanroom";

    public static bool TryParse(string? value, out LoaderKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Vanilla:
                kind = LoaderKind.Vanilla;
                return true;
            case Fabric:
                kind = LoaderKind.Fabric;
                return true;
            case Forge:
                kind = LoaderKind.Forge;
                return true;
            case NeoForge:
                kind = LoaderKind.NeoForge;
                return true;
            case Cleanroom:
                kind = LoaderKind.Cleanroom;
                return true;
            default:
                kind = LoaderKind.Vanilla;
                return false;
        }
    }

    public static string ToId(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => Fabric,
        LoaderKind.Forge => Forge,
        LoaderKind.NeoForge => NeoForge,
        LoaderKind.Cleanroom => Cleanroom,
        _ => Vanilla,
    };

    /// <summary>需要运行安装器 processors 的加载器（Forge 1.13+ 与 NeoForge）。</summary>
    public static bool UsesInstallerProcessors(LoaderKind kind) =>
        kind is LoaderKind.Forge or LoaderKind.NeoForge;

    public static bool IsSupported(string? value) => TryParse(value, out _);
}

public sealed record LoaderVersionInfo(
    string Version,
    bool Stable,
    DateTimeOffset? ReleasedAt);

public sealed record FabricApiVersionInfo(
    string Version,
    string DownloadUrl,
    string FileName,
    string? Sha1,
    long? Size,
    DateTimeOffset ReleasedAt);

public sealed record OptiFineVersionInfo(
    string Type,
    string Patch,
    string FileName,
    DateTimeOffset? ReleasedAt)
{
    public string Key => $"{Type}:{Patch}";
}

public interface ILoaderCatalog
{
    Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetFabricLoadersAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetForgeVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetNeoForgeVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetCleanroomVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FabricApiVersionInfo>>> GetFabricApiVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<Result<string>> GetFabricProfileAsync(
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default);
}
