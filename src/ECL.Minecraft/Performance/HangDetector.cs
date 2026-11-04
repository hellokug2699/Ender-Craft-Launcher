// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine;

namespace Ecl.Minecraft.Performance;

/// <summary>
/// 独立卡死检测状态机（ADR 0041 §3）。只根据可观测证据推进状态：
/// 启动宽限期、Tick 是否推进、MSPT 是否持续异常、进程是否退出。没有 Tick 证据时不会猜测 Hang。
/// </summary>
public sealed class HangDetector(HangThresholds? thresholds = null)
{
    private readonly HangThresholds _thresholds = thresholds ?? HangThresholds.Default;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastTickTimestamp;
    private long? _lastTickCount;
    private int _highMsptSamples;
    private HangPhase _phase = HangPhase.NotStarted;

    public HangPhase Phase => _phase;

    public HangAssessment Observe(PerformanceObservation observation)
    {
        if (!observation.ProcessAlive)
        {
            _phase = HangPhase.Exited;
            return Assessment(
                _phase,
                "游戏进程已退出。",
                "进程不存在，进入正常/异常退出处理。",
                requiresAttention: false,
                EvidenceConfidence.Confirmed);
        }

        if (_startedAt is null)
        {
            _startedAt = observation.Timestamp;
            _phase = HangPhase.Starting;
            return Assessment(
                _phase,
                "检测到游戏进程，处于启动/加载阶段。",
                "尚无稳定 Tick 数据，启动宽限期内不判定卡死。",
                requiresAttention: false,
                EvidenceConfidence.Inferred);
        }

        var elapsed = observation.Timestamp - _startedAt.Value;
        if (elapsed < _thresholds.StartupGrace && observation.TickCount is null)
        {
            _phase = HangPhase.Starting;
            return Assessment(
                _phase,
                "游戏仍在启动/加载阶段。",
                $"启动宽限期内（{elapsed.TotalSeconds:F0}s / {_thresholds.StartupGrace.TotalSeconds:F0}s）。",
                requiresAttention: false,
                EvidenceConfidence.Inferred);
        }

        var tickAdvanced = observation.TickCount is not null &&
                           (_lastTickCount is null || observation.TickCount > _lastTickCount);
        if (tickAdvanced)
        {
            _lastTickCount = observation.TickCount;
            _lastTickTimestamp = observation.Timestamp;
        }
        else if (observation.TickCount is not null && _lastTickTimestamp is not null)
        {
            var stalledFor = observation.Timestamp - _lastTickTimestamp.Value;
            if (stalledFor > _thresholds.SuspectedHangAfter)
            {
                _phase = HangPhase.SuspectedHang;
                return Assessment(
                    _phase,
                    "进程存在，但 Tick 超过阈值未推进。",
                    $"Tick 已 {stalledFor.TotalSeconds:F0}s 未推进；无法由已知暂停解释。",
                    requiresAttention: true,
                    EvidenceConfidence.Confirmed);
            }
        }

        var wasSuspected = _phase == HangPhase.SuspectedHang;
        if (observation.Mspt is { } mspt && mspt >= _thresholds.StallMspt)
        {
            _highMsptSamples++;
            _phase = _highMsptSamples >= _thresholds.ConsecutiveSamples
                ? HangPhase.Stalled
                : HangPhase.Running;
            return Assessment(
                _phase,
                _highMsptSamples >= _thresholds.ConsecutiveSamples
                    ? "MSPT 持续超过 Stall 阈值。"
                    : "检测到短暂 Tick Stall。",
                $"MSPT {mspt:F1}ms，连续异常采样 {_highMsptSamples}/{_thresholds.ConsecutiveSamples}。",
                requiresAttention: _highMsptSamples >= _thresholds.ConsecutiveSamples,
                EvidenceConfidence.Confirmed);
        }

        _highMsptSamples = 0;
        if (observation.Mspt is { } degradeMspt && degradeMspt >= _thresholds.DegradedMspt)
        {
            _phase = HangPhase.Degraded;
            return Assessment(
                _phase,
                "Tick 仍推进，但 MSPT 持续偏高。",
                $"MSPT {degradeMspt:F1}ms 超过正常阈值 {_thresholds.DegradedMspt:F1}ms。",
                requiresAttention: false,
                EvidenceConfidence.Confirmed);
        }

        _phase = HangPhase.Running;
        var evidence = wasSuspected
            ? "Tick 已恢复推进。"
            : observation.TickCount is null
                ? "没有游戏侧 Tick 数据；仅确认进程存活，不能据此声称性能健康。"
                : "Tick 正常推进，未检测到持续异常。";
        return Assessment(
            _phase,
            wasSuspected ? "疑似卡死后已恢复。" : "运行正常。",
            evidence,
            requiresAttention: false,
            observation.TickCount is null ? EvidenceConfidence.Partial : EvidenceConfidence.Confirmed);
    }

    private static HangAssessment Assessment(
        HangPhase phase,
        string reason,
        string evidence,
        bool requiresAttention,
        EvidenceConfidence confidence) =>
        new(phase, reason, [evidence], requiresAttention, confidence);
}
