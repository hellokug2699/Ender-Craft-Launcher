// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.Additions;

/// <summary>
/// 安装计划与依赖解析（ADR 0032 §6）。在下载前拒绝循环依赖、不可满足依赖、Core API 不兼容、
/// 平台不匹配与已安装冲突；可选依赖不会被静默安装。
/// </summary>
public sealed class AdditionPlanner
{
    public Result<AdditionInstallPlan> Plan(
        AdditionCatalog catalog,
        IReadOnlyList<InstalledAddition> installed,
        IReadOnlyList<AdditionId> targets,
        ReleaseVersion coreApiVersion,
        string platform)
    {
        var available = new Dictionary<AdditionId, AdditionEntry>();
        foreach (var entry in catalog.Additions)
        {
            if (!entry.Withdrawn)
            {
                available[entry.Id] = entry;
            }
        }

        var installedById = installed.ToDictionary(item => item.Id);
        var selected = new Dictionary<AdditionId, AdditionEntry>();
        var ordered = new List<AdditionEntry>();
        var visiting = new HashSet<AdditionId>();
        var visited = new HashSet<AdditionId>();
        var reasons = new Dictionary<AdditionId, string>();
        var warnings = new List<string>();

        foreach (var target in targets.Distinct())
        {
            if (!AdditionId.TryParse(target.Value, out var id))
            {
                return Fail("additions.target_invalid", $"Addition ID 无效：{target.Value}");
            }

            if (!available.TryGetValue(id, out var entry))
            {
                return Fail("additions.target_unavailable", $"扩展目录中没有可安装的 {id.Value}（可能已撤回或不存在）。");
            }

            var visit = Visit(entry, "用户选择", required: true);
            if (!visit.IsSuccess)
            {
                return Result<AdditionInstallPlan>.Fail(visit.Error!);
            }
        }

        var install = new List<PlannedAddition>();
        var update = new List<PlannedAddition>();
        var planned = new List<PlannedAddition>();
        foreach (var entry in ordered)
        {
            var reason = reasons.GetValueOrDefault(entry.Id, "依赖");
            if (!installedById.TryGetValue(entry.Id, out var current))
            {
                var item = new PlannedAddition(entry, reason);
                install.Add(item);
                planned.Add(item);
            }
            else if (current.Version.CompareTo(entry.Version) != 0)
            {
                if (!current.Enabled)
                {
                    warnings.Add($"{entry.Id.Value} 已安装但处于禁用状态；本次更新不会自动启用。");
                }

                var item = new PlannedAddition(entry, reason);
                update.Add(item);
                planned.Add(item);
            }
        }

        return Result<AdditionInstallPlan>.Ok(
            new AdditionInstallPlan(install, update, [], warnings) { Ordered = planned });

        Result Visit(AdditionEntry entry, string reason, bool required)
        {
            if (visited.Contains(entry.Id))
            {
                if (selected.TryGetValue(entry.Id, out var existing) && existing.Version.CompareTo(entry.Version) != 0)
                {
                    return Invalid(
                        "additions.dependency_conflict",
                        $"依赖 {entry.Id.Value} 需要两个不同版本：{existing.Version} 与 {entry.Version}。");
                }

                return Result.Ok();
            }

            if (!visiting.Add(entry.Id))
            {
                return Invalid("additions.dependency_cycle", $"检测到循环依赖：{entry.Id.Value}。");
            }

            if (!entry.CoreApi.Contains(coreApiVersion))
            {
                return Invalid(
                    "additions.incompatible_core_api",
                    $"扩展 {entry.Id.Value} 需要 Core API {entry.CoreApi.Min}-{entry.CoreApi.Max}，当前为 {coreApiVersion}。");
            }

            if (!entry.SupportsPlatform(platform))
            {
                return Invalid(
                    "additions.platform_unsupported",
                    $"扩展 {entry.Id.Value} 不支持当前平台 {platform}。");
            }

            foreach (var conflict in entry.ConflictsWith)
            {
                if (installedById.ContainsKey(conflict) || selected.ContainsKey(conflict))
                {
                    return Invalid(
                        "additions.conflict_declared",
                        $"扩展 {entry.Id.Value} 与已安装或计划安装的 {conflict.Value} 冲突。");
                }
            }

            foreach (var other in installed)
            {
                var installedEntry = catalog.Additions.FirstOrDefault(item => item.Id == other.Id);
                if (installedEntry is not null && installedEntry.ConflictsWith.Contains(entry.Id))
                {
                    return Invalid(
                        "additions.conflict_declared",
                        $"已安装的 {other.Id.Value} 声明与 {entry.Id.Value} 冲突。");
                }
            }

            selected[entry.Id] = entry;
            reasons[entry.Id] = reason;

            foreach (var dependency in entry.Dependencies)
            {
                if (!available.TryGetValue(dependency.Id, out var availableDependency))
                {
                    if (dependency.Optional)
                    {
                        warnings.Add($"{entry.Id.Value} 的可选依赖 {dependency.Id.Value} 不在目录中，已跳过。");
                        continue;
                    }

                    return Invalid(
                        "additions.dependency_missing",
                        $"扩展 {entry.Id.Value} 缺少必需依赖 {dependency.Id.Value}。");
                }

                if (installedById.TryGetValue(dependency.Id, out var installedDependency) &&
                    dependency.IsSatisfiedBy(installedDependency.Version))
                {
                    // 已安装版本满足范围，保留现状，不强制升级。
                    continue;
                }

                if (!dependency.IsSatisfiedBy(availableDependency.Version))
                {
                    return Invalid(
                        "additions.dependency_version_unsatisfied",
                        $"扩展 {entry.Id.Value} 需要 {dependency.Id.Value} {dependency.MinVersion}" +
                        $"{(dependency.MaxVersion is null ? "+" : $"-{dependency.MaxVersion}")}，" +
                        $"目录版本为 {availableDependency.Version}。");
                }

                if (dependency.Optional)
                {
                    warnings.Add($"{entry.Id.Value} 的可选依赖 {dependency.Id.Value} 可用但不会自动安装。");
                    continue;
                }

                var nested = Visit(availableDependency, $"依赖 {entry.Id.Value}", required: true);
                if (!nested.IsSuccess)
                {
                    return nested;
                }
            }

            visiting.Remove(entry.Id);
            visited.Add(entry.Id);
            ordered.Add(entry);
            return Result.Ok();
        }

        static Result Invalid(string code, string message) =>
            Result.Fail(code, message);

        static Result<AdditionInstallPlan> Fail(string code, string message) =>
            Result<AdditionInstallPlan>.Fail(code, message);
    }
}
