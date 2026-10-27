// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public static class VersionMerger
{
    public static MinecraftVersion Merge(MinecraftVersion parent, MinecraftVersion child)
    {
        var parentLibraries = new Dictionary<string, VersionLibrary>(StringComparer.Ordinal);
        foreach (var library in parent.Libraries)
        {
            parentLibraries[library.Name] = library;
        }

        var mergedLibraries = new List<VersionLibrary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 加载器提供的库必须排在原版库之前：Forge 1.7.10 用 guava 17.0 覆盖原版 15.0，
        // 若父版本先进入类路径，旧版本会先被加载并导致 NoSuchMethodError。
        foreach (var library in child.Libraries)
        {
            if (!seen.Add(library.Name))
            {
                continue;
            }

            mergedLibraries.Add(MergeLibrary(
                parentLibraries.TryGetValue(library.Name, out var inherited) ? inherited : null,
                library));
        }

        foreach (var library in parent.Libraries)
        {
            if (seen.Add(library.Name))
            {
                mergedLibraries.Add(library);
            }
        }

        return new MinecraftVersion
        {
            Id = child.Id,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            InheritsFrom = null,
            Assets = child.Assets ?? parent.Assets,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Downloads = child.Downloads ?? parent.Downloads,
            Logging = MergeLogging(parent.Logging, child.Logging),
            Libraries = mergedLibraries,
        };
    }

    /// <summary>
    /// arguments.game / arguments.jvm 是追加语义。只要子版本提供了其中任意一项，
    /// 都需要保留父版本的参数（例如原版的 -Djna.tmpdir、-Xss1M 与标准游戏参数）。
    /// </summary>
    private static VersionArguments? MergeArguments(VersionArguments? parent, VersionArguments? child)
    {
        if (parent is null)
        {
            return child;
        }

        if (child is null)
        {
            return parent;
        }

        return new VersionArguments
        {
            Game = parent.Game.Concat(child.Game).ToList(),
            Jvm = parent.Jvm.Concat(child.Jvm).ToList(),
        };
    }

    /// <summary>
    /// Forge 1.12.2 等版本 JSON 中的 "logging": {} 是空对象，不应覆盖父版本有效的日志配置。
    /// </summary>
    private static VersionLogging? MergeLogging(VersionLogging? parent, VersionLogging? child)
    {
        if (HasUsableLogging(child))
        {
            return child;
        }

        return HasUsableLogging(parent) ? parent : child ?? parent;
    }

    private static bool HasUsableLogging(VersionLogging? logging) =>
        logging?.Client is { } client &&
        (client.File is { Id.Length: > 0 } || !string.IsNullOrWhiteSpace(client.Argument));

    /// <summary>
    /// 子版本用同名库覆盖父版本时，如果子条目没有下载地址，保留父条目的地址。
    /// 旧版 Forge 的 versionInfo 会省略部分原版库的 URL。
    /// </summary>
    private static VersionLibrary MergeLibrary(VersionLibrary? parent, VersionLibrary child)
    {
        if (parent is null)
        {
            return child;
        }

        var childHasDownloads = child.Downloads is not null &&
                                (child.Downloads.Artifact is not null ||
                                 child.Downloads.Classifiers is { Count: > 0 });
        if (!childHasDownloads && string.IsNullOrWhiteSpace(child.Url))
        {
            child.Downloads = parent.Downloads;
            child.Url = parent.Url;
            child.Sha1 ??= parent.Sha1;
            child.Size ??= parent.Size;
        }

        return child;
    }
}
