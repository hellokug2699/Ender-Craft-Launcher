// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Worlds;

namespace Ecl.World.Dimensions;

/// <summary>
/// 把维度解析结果投影为工作台使用的 <see cref="DimensionInfo"/>。
/// 只有身份已确认时目录字段才对读取开放；冲突维度不暴露任何可写/可读目录。
/// </summary>
public static class DimensionProjection
{
    public static DimensionInfo ToDimensionInfo(DimensionDescriptor descriptor)
    {
        string? Readable(DimensionStorageCategory category) =>
            descriptor.FindReadable(category)?.AbsolutePath;

        return new DimensionInfo(
            descriptor.Id.Value,
            descriptor.DisplayName,
            descriptor.Kind,
            descriptor.RootDirectory,
            Readable(DimensionStorageCategory.Region),
            Readable(DimensionStorageCategory.Entities),
            Readable(DimensionStorageCategory.Poi))
        {
            Resolution = descriptor.Resolution,
            StorageMappings = descriptor.Storage,
            SupportNote = descriptor.Note,
            DeclaredByLevelDat = descriptor.DeclaredByLevelDat,
        };
    }
}
