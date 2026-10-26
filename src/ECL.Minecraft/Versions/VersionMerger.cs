// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public static class VersionMerger
{
    public static MinecraftVersion Merge(MinecraftVersion parent, MinecraftVersion child)
    {
        var libraries = new Dictionary<string, VersionLibrary>(StringComparer.Ordinal);
        foreach (var library in parent.Libraries)
        {
            libraries[library.Name] = library;
        }

        foreach (var library in child.Libraries)
        {
            libraries[library.Name] = library;
        }

        return new MinecraftVersion
        {
            Id = child.Id,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            InheritsFrom = null,
            Assets = child.Assets ?? parent.Assets,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = child.Arguments ?? parent.Arguments,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Downloads = child.Downloads ?? parent.Downloads,
            Logging = child.Logging ?? parent.Logging,
            Libraries = libraries.Values.ToList(),
        };
    }
}
