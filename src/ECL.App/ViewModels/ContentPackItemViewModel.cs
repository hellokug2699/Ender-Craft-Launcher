// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 资源包/光影包列表的一行；在 B2b 拆出独立页面之前继续由模组页承载，
/// 避免旧能力在重写过程中丢失。
/// </summary>
public sealed class ContentPackItemViewModel
{
    private readonly ContentPackEntry _entry;
    private readonly ILocalizer _localizer;

    public ContentPackItemViewModel(ContentPackEntry entry, ILocalizer localizer)
    {
        _entry = entry;
        _localizer = localizer;
    }

    public string FileName => _entry.FileName;

    public bool Enabled => _entry.Enabled;

    /// <summary>
    /// 资源包名称来自 pack.mcmeta，可能是多行 JSON 片段或带格式码的长文本；
    /// 列表行只显示单行摘要，完整内容留给 Tooltip，避免撑高表格。
    /// </summary>
    public string Name => Summarize(_entry.Name);

    private static string Summarize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var parts = value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var singleLine = string.Join(" ", parts);
        return singleLine.Length <= 160 ? singleLine : $"{singleLine[..157]}…";
    }

    public string StatusText
    {
        get
        {
            var state = _localizer.Get(Enabled ? "mods.packEnabledLabel" : "mods.packDisabledLabel");
            return _entry.Incompatible
                ? $"{state} · {_localizer.Get("mods.packIncompatibleLabel")}"
                : state;
        }
    }

    public string Meta => _entry.PackFormat is null
        ? _entry.FileName
        : $"{_entry.FileName} · pack_format {_entry.PackFormat}";
}
