// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Sessions;

namespace Ecl.App.ViewModels.LogSessions;

/// <summary>
/// 诊断证据条目。归属置信度必须显示本地化文案与原因，不能把共享/可能归属
/// 的证据显示为“本次启动日志”（logs.md §4.2）。
/// </summary>
public sealed class LogEvidenceRowViewModel
{
    private readonly ILocalizer _localizer;

    public LogEvidenceRowViewModel(SessionEvidenceItem item, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(item);
        _localizer = localizer;
        Artifact = item.Artifact;
        Availability = item.Availability;
        Size = item.Size;
        LastWriteTimeUtc = item.LastWriteTimeUtc;
        Path = item.Path;
        Detail = item.Detail;
    }

    public SessionArtifact Artifact { get; }

    public SessionSourceAvailability Availability { get; }

    public long? Size { get; }

    public DateTimeOffset? LastWriteTimeUtc { get; }

    public string? Path { get; }

    public string? Detail { get; }

    public string Id => Artifact.Id;

    public string KindText => _localizer.Get("logs.evidence.kind." + Artifact.Kind);

    public string OwnershipText => _localizer.Get("logs.ownership." + Artifact.Ownership);

    public string ConfidenceText => _localizer.Get("logs.ownership.description." + Artifact.Ownership);

    public string AvailabilityText => _localizer.Get("logs.source.availability." + Availability);

    public string SizeText => Size is { } size
        ? Ecl.App.Services.StorageSizeFormatter.FormatBytes(size)
        : _localizer.Get("logs.sizeUnknown");

    public string TimeText => LastWriteTimeUtc is { } time
        ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : _localizer.Get("logs.timeUnknown");

    public string Summary => $"{AvailabilityText} · {SizeText} · {TimeText}";

    public string WarningText => string.IsNullOrWhiteSpace(Artifact.Warning) ? string.Empty : Artifact.Warning;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Artifact.Warning);

    public bool IsReadable => Availability is
        SessionSourceAvailability.Available or SessionSourceAvailability.Partial;
}
