// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// 实例附加组件摘要的唯一格式化实现（B2 步骤 3b-4a）。
///
/// 实例列表、基本信息页与「修改」页都要显示“这个实例装了哪些 ECL 可识别的组件”；
/// 三处必须给出同一套文案与顺序，因此格式化不再由其中一个 ViewModel 私有持有。
/// 这里只做呈现，不判断组件是否可用。
/// </summary>
public static class InstanceComponentsPresentation
{
    public static string Describe(GameInstance model, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(localizer);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.FabricApiVersion))
        {
            parts.Add($"Fabric API {model.FabricApiVersion}");
        }

        if (!string.IsNullOrWhiteSpace(model.OptiFineVersion))
        {
            parts.Add($"OptiFine {model.OptiFineVersion}");
        }

        return parts.Count == 0
            ? string.Empty
            : localizer.Get("instances.components.summary", string.Join(" · ", parts));
    }
}
