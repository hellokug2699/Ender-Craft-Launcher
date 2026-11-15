// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Java;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// Java 管理列表的一行（页面规格 §3.2）。
///
/// 只呈现领域层给出的运行时事实：主版本、名称（发行版 + 完整版本）、路径、
/// 验证状态与卸载阻断原因。名称优先使用真实验证输出，未验证时回退到 release
/// 元数据并保持“未验证”状态，不把目录名推断当成验证结论。
/// </summary>
public sealed partial class JavaManagementRowViewModel(
    JavaRuntime runtime,
    JavaRuntimeOrigin origin,
    ILocalizer localizer) : ObservableObject
{
    private JavaRuntimeValidation _validation = JavaRuntimeValidation.Unknown;
    private IReadOnlyList<JavaUninstallBlocker> _blockers = [];
    private bool _blockersKnown;

    public JavaRuntime Runtime { get; } = runtime;

    public JavaRuntimeOrigin Origin { get; } = origin;

    public string ExecutablePath => Runtime.ExecutablePath;

    public string HomePath => Runtime.HomePath;

    public string Path => Runtime.HomePath;

    public bool IsManaged => Origin == JavaRuntimeOrigin.EclManaged;

    public string OriginText => Origin switch
    {
        JavaRuntimeOrigin.EclManaged => localizer.Get("java.page.origin.managed"),
        JavaRuntimeOrigin.UserImported => localizer.Get("java.page.origin.imported"),
        _ => localizer.Get("java.page.origin.discovered"),
    };

    public string Name
    {
        get
        {
            var vendor = _validation.Vendor ?? Runtime.Vendor;
            var rawVersion = _validation.Version?.Raw ?? Runtime.Version?.Raw;
            var displayVendor = string.IsNullOrWhiteSpace(vendor)
                ? localizer.Get("java.page.unknownDistribution")
                : vendor;
            return rawVersion is null
                ? displayVendor
                : $"{displayVendor} {rawVersion}";
        }
    }

    public string VersionText => _validation.Version?.Raw ?? Runtime.Version?.Raw
                                 ?? localizer.Get("java.page.unknownVersion");

    public string MajorVersionText
    {
        get
        {
            var major = _validation.Version?.Major ?? Runtime.Version?.Major;
            return major is null
                ? localizer.Get("java.page.unknownVersion")
                : major.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public JavaRuntimeValidationState State => _validation.State;

    public bool IsValidating => State == JavaRuntimeValidationState.Validating;

    public string StateText => State switch
    {
        JavaRuntimeValidationState.Available => localizer.Get("java.page.state.available"),
        JavaRuntimeValidationState.Validating => localizer.Get("java.page.state.validating"),
        JavaRuntimeValidationState.Unsupported32Bit => localizer.Get("java.manager.unsupported32"),
        JavaRuntimeValidationState.Failed => localizer.Get("java.page.state.failed"),
        _ => localizer.Get("java.page.state.unknown"),
    };

    public string StateDetail => DescribeFailure(_validation);

    public bool HasStateDetail => !string.IsNullOrEmpty(StateDetail);

    /// <summary>是否已经确认过卸载阻断原因；未确认时按钮保持禁用并说明正在确认。</summary>
    public bool BlockersKnown => _blockersKnown;

    public bool CanUninstall => IsManaged && _blockersKnown && _blockers.Count == 0;

    public string UninstallHint
    {
        get
        {
            if (!IsManaged)
            {
                return localizer.Get("java.page.blocker.notManaged");
            }

            if (!_blockersKnown)
            {
                return localizer.Get("java.page.uninstall.checking");
            }

            return _blockers.Count == 0
                ? localizer.Get("java.page.uninstall.available")
                : BlockerSummary;
        }
    }

    public string BlockerSummary => string.Join(
        " ",
        _blockers.Select(blocker => blocker.Kind switch
        {
            JavaUninstallBlockerKind.NotManaged => localizer.Get("java.page.blocker.notManaged"),
            JavaUninstallBlockerKind.PathUnsafe => localizer.Get("java.page.blocker.pathUnsafe"),
            JavaUninstallBlockerKind.InstancePinned => localizer.Get(
                "java.page.blocker.instancePinned",
                blocker.Detail),
            JavaUninstallBlockerKind.GameRunning => localizer.Get(
                "java.page.blocker.gameRunning",
                blocker.Detail),
            _ => localizer.Get("java.page.blocker.usageUnknown"),
        }));

    public JavaRuntimeValidation Validation => _validation;

    public void MarkValidating()
    {
        _validation = new JavaRuntimeValidation(
            JavaRuntimeValidationState.Validating,
            null,
            null,
            null,
            JavaRuntimeValidationFailure.None,
            null,
            TimeSpan.Zero);
        NotifyValidationChanged();
    }

    public void ApplyValidation(JavaRuntimeValidation validation)
    {
        _validation = validation;
        NotifyValidationChanged();
    }

    public void ApplyUninstallPlan(JavaUninstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _blockers = plan.Blockers;
        _blockersKnown = true;
        OnPropertyChanged(nameof(BlockersKnown));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(UninstallHint));
        OnPropertyChanged(nameof(BlockerSummary));
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsValidating));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(HasStateDetail));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(MajorVersionText));
    }

    private string DescribeFailure(JavaRuntimeValidation validation) => validation.Failure switch
    {
        JavaRuntimeValidationFailure.None => string.Empty,
        JavaRuntimeValidationFailure.NotStarted => localizer.Get("java.page.failure.notStarted"),
        JavaRuntimeValidationFailure.TimedOut => localizer.Get("java.page.failure.timedOut"),
        JavaRuntimeValidationFailure.ExitCode => localizer.Get(
            "java.page.failure.exitCode",
            validation.Detail ?? string.Empty),
        JavaRuntimeValidationFailure.Unreadable => localizer.Get(
            "java.page.failure.unreadable",
            validation.Detail ?? string.Empty),
        _ => string.Empty,
    };
}
