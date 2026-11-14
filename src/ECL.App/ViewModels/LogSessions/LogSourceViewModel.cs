// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Sessions;

namespace Ecl.App.ViewModels.LogSessions;

/// <summary>来源切换栏中的一项；不可用来源保留可见状态并说明原因。</summary>
public sealed record LogSourceViewModel(
    SessionLogSourceKind Kind,
    string Title,
    bool IsAvailable,
    string StatusText,
    string? Warning,
    ArtifactOwnership? Ownership)
{
    public bool IsGameLog => Kind == SessionLogSourceKind.GameLog;

    public bool IsEvidence => Kind == SessionLogSourceKind.Evidence;
}
