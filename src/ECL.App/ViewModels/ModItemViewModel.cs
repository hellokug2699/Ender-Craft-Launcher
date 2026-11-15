// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 模组表格的一行（B2 步骤 3b-3）。只读投影 <see cref="ModEntry"/>：
/// 启用、更新、删除都通过页面的领域服务执行，行对象不直接写文件。
/// </summary>
public sealed partial class ModItemViewModel : ObservableObject
{
    private readonly ModEntry _entry;
    private readonly ILocalizer _localizer;

    public ModItemViewModel(ModEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);

        _entry = entry;
        _localizer = localizer;
        StatusBadges = ModStatusPresentation.Create(entry, localizer);
        LastWriteTimeUtc = TryReadLastWriteTime(entry.FullPath);
    }

    public string FileName => _entry.FileName;

    public string? ModId => _entry.Metadata?.ModId;

    public bool Enabled => _entry.Enabled;

    public IReadOnlyList<ModIssue> Issues => _entry.Issues;

    public IReadOnlyList<ModStatusBadge> StatusBadges { get; }

    /// <summary>文件最后写入时间；“时间顺序/时间倒序”排序使用；文件不可读时为 <c>null</c>。</summary>
    public DateTime? LastWriteTimeUtc { get; }

    public string Name => _entry.Metadata?.Name ?? _entry.FileName;

    public string PlatformText => _entry.Metadata?.Platform switch
    {
        ModPlatform.Fabric => _localizer.Get("mods.platform.fabric"),
        ModPlatform.Forge => _localizer.Get("mods.platform.forge"),
        ModPlatform.NeoForge => _localizer.Get("mods.platform.neoforge"),
        ModPlatform.LegacyForge => _localizer.Get("mods.platform.legacyForge"),
        _ => _localizer.Get("mods.platform.unknown"),
    };

    /// <summary>
    /// 适用 Minecraft 版本：领域模型没有独立的 MC 版本字段，只能从 <c>minecraft</c> 依赖的
    /// 版本范围派生；缺失时显示“未知”，不猜测、不伪造。
    /// </summary>
    public string MinecraftVersionText
    {
        get
        {
            var range = _entry.Metadata?.Dependencies
                .FirstOrDefault(dependency => string.Equals(dependency.ModId, "minecraft", StringComparison.OrdinalIgnoreCase))
                ?.VersionRange;
            return string.IsNullOrWhiteSpace(range)
                ? _localizer.Get("mods.version.unknown")
                : range.Trim();
        }
    }

    /// <summary>“适用版本”列：Minecraft 版本范围 + 加载器；未知模组不拼接伪信息。</summary>
    public string ApplicableVersionText => _entry.Metadata is null
        ? _localizer.Get("mods.version.unknown")
        : $"{MinecraftVersionText} · {PlatformText}";

    public string ToggleLabel => _localizer.Get(Enabled ? "mods.row.disable" : "mods.row.enable");

    public bool HasIssues => _entry.Issues.Count > 0;

    /// <summary>行内问题的逐条说明，用于文件名单元格 Tooltip。</summary>
    public string IssuesText => string.Join(
        Environment.NewLine,
        _entry.Issues.Select(issue => ModStatusPresentation.Describe(issue, _localizer)));

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { FileName };
            if (_entry.Metadata is { } metadata)
            {
                lines.Add($"{metadata.Name} · {metadata.ModId} · {metadata.Version}");
            }
            else if (!string.IsNullOrWhiteSpace(_entry.Error))
            {
                lines.Add(_entry.Error);
            }

            if (IssuesText.Length > 0)
            {
                lines.Add(IssuesText);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>批量选择状态（表格左侧复选列之外的行选择）；由页面命令统一处理。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>搜索只匹配英文名称、文件名与 Mod ID（mods.md §4 仅支持英文）。</summary>
    public bool MatchesSearch(string search)
    {
        ArgumentNullException.ThrowIfNull(search);

        return Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || (ModId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public bool MatchesFilter(ModStatusFilter filter) => filter switch
    {
        ModStatusFilter.Available => Enabled && _entry.Metadata is not null && _entry.Issues.Count == 0,
        ModStatusFilter.Disabled => !Enabled,
        ModStatusFilter.Dangerous => StatusBadges.Any(badge => badge.Kind == ModStatusKind.Dangerous),
        ModStatusFilter.Abnormal => StatusBadges.Any(badge => badge.Kind is ModStatusKind.PossiblyAbnormal
            or ModStatusKind.Unknown
            or ModStatusKind.Conflict
            or ModStatusKind.MissingDependency),
        // 更新来源/逐模组检查更新能力尚不存在（HANDOFF §5.2）：筛选结构保留但不会产生命中。
        ModStatusFilter.Updatable => false,
        _ => false,
    };

    /// <summary>切换启用状态失败后，让复选框重新读取真实状态。</summary>
    public void NotifyEnabledChanged() => OnPropertyChanged(nameof(Enabled));

    public override string ToString() => FileName;

    private static DateTime? TryReadLastWriteTime(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
