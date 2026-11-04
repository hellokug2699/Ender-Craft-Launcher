// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>
/// 诊断模块的隐私边界（ADR 0033 §9）。
/// <para>
/// 日志解析、崩溃报告解析、规则匹配、报告生成和历史读写全部在本地完成；
/// 本程序集不包含网络客户端，不会因为用户点击“分析”而上传日志、崩溃报告、
/// 环境信息、模组列表、世界数据或诊断历史。
/// </para>
/// <para>
/// 未来若增加主动分享能力，必须是独立功能：上传前提供内容预览、敏感信息检测与脱敏，
/// 并由用户明确确认。当前 <see cref="NetworkAccessRequired"/> 恒为 <c>false</c>。
/// </para>
/// </summary>
public static class DiagnosticPrivacy
{
    /// <summary>诊断核心不需要任何网络访问。</summary>
    public const bool NetworkAccessRequired = false;

    /// <summary>面向 UI/文档的隐私声明文本。</summary>
    public const string Policy =
        "诊断在本地执行：不发起网络请求，也不上传日志、崩溃报告、环境信息或诊断历史。";

    /// <summary>历史存储只保存诊断结论与证据摘录，不保存原始日志副本。</summary>
    public const string HistoryPolicy =
        "诊断历史只保存报告、环境摘要、知识库版本和证据摘录，不保存原始日志副本。";
}
