// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Wardrobe.Png;

namespace Ecl.Wardrobe;

/// <summary>皮肤模型启发式推断结果；<see cref="SkinModel.Unknown"/> 表示无法可靠判断。</summary>
public sealed record SkinModelInference
{
    public required SkinModel Model { get; init; }

    /// <summary>面向用户/诊断的说明，必须解释为什么只能得到这个结果。</summary>
    public required string Reason { get; init; }

    public bool IsConclusive => Model != SkinModel.Unknown;
}

/// <summary>
/// 根据 64x64 现代皮肤布局推断 Classic / Slim。
/// <para>
/// 重要：皮肤模型本质上是账户档案（textures 元数据中的 <c>model</c>）或启动器配置中的元数据，
/// PNG 图片本身并不携带“Classic/Slim”字段。这里只是利用模型布局差异做启发式判断：
/// 细手臂（Slim）在 64x64 纹理中不会使用手臂外侧的若干列，因此这些像素应为全透明；
/// 经典手臂（Classic）则通常会绘制这些列。用户仍应在上传时确认模型。
/// </para>
/// </summary>
public static class SkinModelHeuristic
{
    /// <summary>当前启发式只适用于 64x64 的现代皮肤格式。</summary>
    public const int ModernSkinSize = 64;

    /// <summary>
    /// 两条手臂外侧、仅 Classic 4px 手臂会使用的区域：
    /// 右臂 x=54..55 / y=20..31，左臂 x=46..47 / y=52..63。
    /// 每行合计 4 个像素，因此常被概括为“手臂 4px 列”。
    /// </summary>
    private static readonly ArmRegion[] OuterArmRegions =
    [
        new(54, 20, 2, 12),
        new(46, 52, 2, 12),
    ];

    public static SkinModelInference Infer(PngImage? image)
    {
        if (image is null)
        {
            return new SkinModelInference
            {
                Model = SkinModel.Unknown,
                Reason = "没有可用的解码图像，无法判断皮肤模型。",
            };
        }

        if (image.Width != ModernSkinSize || image.Height != ModernSkinSize)
        {
            return new SkinModelInference
            {
                Model = SkinModel.Unknown,
                Reason = $"仅对 64x64 皮肤做模型启发式；当前图片为 {image.Width}x{image.Height}。",
            };
        }

        var allTransparent = true;
        var allOpaque = true;

        foreach (var region in OuterArmRegions)
        {
            for (var y = region.Y; y < region.Y + region.Height; y++)
            {
                for (var x = region.X; x < region.X + region.Width; x++)
                {
                    var alpha = image.GetPixel(x, y).A;
                    if (alpha != 0)
                    {
                        allTransparent = false;
                    }

                    if (alpha != 255)
                    {
                        allOpaque = false;
                    }
                }
            }
        }

        if (allTransparent)
        {
            return new SkinModelInference
            {
                Model = SkinModel.Slim,
                Reason = "64x64 皮肤外侧手臂列为全透明，符合 Slim（3px 手臂）布局。",
            };
        }

        if (allOpaque)
        {
            return new SkinModelInference
            {
                Model = SkinModel.Classic,
                Reason = "64x64 皮肤外侧手臂列已绘制，符合 Classic（4px 手臂）布局。",
            };
        }

        return new SkinModelInference
        {
            Model = SkinModel.Unknown,
            Reason = "64x64 皮肤外侧手臂列只有部分透明，无法可靠区分 Classic 与 Slim。",
        };
    }

    private readonly record struct ArmRegion(int X, int Y, int Width, int Height);
}
