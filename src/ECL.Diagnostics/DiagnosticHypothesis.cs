// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>
/// 一条候选原因（推断）。与 <see cref="DiagnosticEvidence"/> 分开保存：
/// 证据是“文件里确实出现了什么”，假设是“这可能意味着什么”，后者必须带有置信度与缺失证据说明。
/// </summary>
public sealed record DiagnosticHypothesis(
    string Statement,
    DiagnosticConfidence Confidence,
    IReadOnlyList<DiagnosticEvidence> SupportingEvidence,
    IReadOnlyList<string> MissingEvidence);
