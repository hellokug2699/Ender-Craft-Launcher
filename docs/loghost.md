# LogHost：独立日志采集宿主

本页描述 ADR 0019 的进程边界：游戏进程、日志采集与主 UI 生命周期解耦。

## 1. 拓扑

```text
ECL.UI (WPF)  ──Named Pipe──▶  ECL.LogHost  ──stdout/stderr──▶  Minecraft (Java)
                                  │
                                  └─▶ logs/sessions/<id>/game.stdout.log / stderr.log
```

- 游戏进程由 LogHost 创建，stdout/stderr 唯一读取者始终是 LogHost。
- 主 UI 只通过命名管道发送控制消息，并从会话文件读取日志内容。
- 主 UI 退出、崩溃或重启都不会终止 LogHost，也不会中断活动会话的采集。

## 2. 协议

- 命名管道 `ecl-loghost-v1`，`PipeOptions.CurrentUserOnly` 限制为当前用户。
- 帧格式：4 字节小端长度 + UTF-8 JSON；单帧上限 1 MiB。
- 消息集：`hello`、`startProcess`、`processStarted`、`getState`、`state`、`flush`、`flushed`、`shutdown`、`shuttingDown`、`error`。
- 每个请求带 `requestId`，客户端按 ID 关联响应；超时 30 秒。
- 协议只传控制与状态，不传历史日志正文。

## 3. 生命周期

- 客户端首次连接时按需启动 `ECL.LogHost.exe`；同一用户通过命名互斥量保证单实例。
- LogHost 启动游戏时：校验会话存在 → 创建进程 → 记录 PID/启动时间/可执行文件 → 持续排空管道。
- 退出后：排空 → 刷新 → 记录退出码 → 登记崩溃证据 → `CaptureCompleted` / `CaptureIncomplete`。
- 存在活动会话时拒绝 `shutdown`；无客户端且无活动会话超过空闲时间（默认 300 秒）后自行退出。
- 主程序找不到 LogHost 时降级为进程内采集，并在会话 manifest 写入 `CaptureFault`，不伪装成正常隔离。

## 4. 资源与背压

- 每条管道使用 `SessionLogWriter` 有界队列（默认 1024 块），饱和时丢弃并记录字节数。
- 日志流式写盘，不在内存中保留完整日志。
- 内存压力等级由主程序 `MemoryPressureMonitor` 采样并分级；`Critical` 时日志视图只保留有限行、降低刷新，LogHost 的最小采集路径不受影响。

## 5. 内存压力分级

| 等级 | 行为 |
|---|---|
| Normal | 常规缓存与 1 秒刷新 |
| Elevated | 限制日志视图行数与刷新频率，停止非必要预加载 |
| Critical | 进一步压缩视图、暂停非必要刷新，仅保留最近日志 |
| Recovery | 达到保持时间后分批恢复，避免缓存重建反弹 |

阈值与保持时间由 `MemoryPressureStateMachine` 参数化，默认值需在实机基准后调整；不以 CPU 优先级冒充内存保护。

## 6. 部署与版本

- LogHost 从当前版本目录随 ECL 一起部署；活动会话期间 Bootstrapper 不应删除其所在版本目录。
- 协议带版本号；版本不兼容时优先保证已有会话继续采集，UI 显示兼容性警告。
- 可通过 `ECL_LOG_HOST_PATH` 指定 LogHost 可执行文件路径，便于测试与自定义部署。

## 7. 尚未完成

- LogHost 崩溃后，主程序会把仍在运行的会话标记为 `CaptureIncomplete` 并记录 `CaptureFault`；文件日志回退与 `RecoveryPending` 自动恢复流程仍未实现。
- 多 UI 客户端并发订阅与增量状态推送。
- 系统关机/注销时的最终 flush 与中断标记。
- 压力测试：高频日志 + 慢盘 + 队列上限的基准数值。

## 8. 实机验证（2026-11-02）

- 使用 ECL_LOG_HOST_PATH 指向独立宿主启动原版实例：manifest 的 captureHost 记录为 loghost，没有 captureFault。
- 游戏运行期间关闭 CLI 进程不影响采集；正常退出后会话由 LogHost 收尾为 CaptureCompleted。
- 首次连接若超过客户端等待窗口会降级为进程内采集并写入 CaptureFault；连接等待窗口随后调整为 15 秒，实机复测走 LogHost 路径。
