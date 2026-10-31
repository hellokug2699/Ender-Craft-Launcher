# 任务进度与诊断（Operation）

ECL 的长时间任务（下载、安装、导入、备份、启动…）统一通过 Operation 模型对外表达进度与失败原因。
UI 和 CLI 只消费快照，不直接依赖 `DownloadService`，也不解析异常文本判断错误类型。

本文档对应第一阶段：**模型 + 下载接入 + 最小 UI 闭环**。安装器、整合包、Java、备份等业务服务仍使用
现有 `IProgress<T>` API，后续逐步迁移，不在此阶段做全仓库重构。

## 1. 分层

```text
DownloadService / Business Service
        ↓
IOperationService / OperationHandle        （写入端）
        ↓
IOperationStore（内存快照 + Changed 事件）   （读取端）
        ↓
OperationCenterViewModel / CLI / Logging
```

- `ECL.Operations` 不依赖 Core、Download 或 WPF，可被任意业务模块引用。
- `ECL.Download` 引用 `ECL.Operations`，在 `AddEclDownload()` 中自动注册任务中心。
- UI 只注入 `IOperationStore`；写入端（`IOperationService`）与读取端解析到同一个进程内实例。

## 2. 模型

### 状态

```text
Pending → Running ⇄ Retrying → Completed / Failed / Cancelled
```

- `Pending` 预留给排队任务；第一阶段从 `Running` 开始。
- `Paused` 不是状态：暂停是运行控制，用 `OperationSnapshot.IsPaused` 表达，任务仍在 `Running`。
- `Stalled` 也不是状态：`IsStalled` 表示运行中长时间没有进度，不自动取消、不算失败。
- `Retrying` 是短暂状态：下一次进度上报会回到 `Running`。

### 快照字段

`OperationSnapshot` 包含：`Id`、`Kind`、`Title`、`State`、`StageId/StageText`、`CurrentItem`、
`Completed/Total`（项目数）、`BytesDownloaded/TotalBytes`、`BytesPerSecond`、`Eta`、`Source`、
`RetryAttempt/MaxRetryAttempts/RetryReason`、`Error`、`CanCancel/CanRetry`、`IsPaused`、`IsStalled`、
`StartedAt/UpdatedAt`。

- `Progress` 只表示**字节进度**；总大小未知时为 `null`（indeterminate），不用文件数量冒充。
- `ItemProgress` 表示项目数量进度，供 UI 在字节数未知时显示 `12/40 项`。
- `Eta` 仅在速度与总大小都已知时计算。

### 错误归因

`OperationErrorKind`：`Network`、`Upstream`、`Authentication`、`FileIntegrity`、`LocalIO`、
`Configuration`、`EclInternal`、`Cancelled`。`OperationErrors.Classify` 把异常映射为稳定归因；
多个失败同时存在时选择最需要用户处理的原因（例如认证优先于网络）。

### 取消与重试

`IOperationControl` 由任务提供者实现：`TryCancel()` 让提供者停止工作，`Retry` 提供重新执行的工厂。
Operation 层只转交请求并更新 `CanCancel` / `CanRetry`，不猜测业务语义。
下载批次注册内部取消源与重试工厂；重试会生成新的 Operation，旧失败记录保留，且只允许启动一次。

## 3. 下载接入

`DownloadAsync` 每个批次自动登记一个 `Download` 任务：

- 标题取第一个文件的显示名（多文件追加 `(+N)`），来源取下载 URL 的主机名。
- 流式读取时按约 150 ms 节流上报**当前文件已写字节**，不再等整个文件完成。
- `SpeedMeter` 使用指数平滑（EMA），避免单个大文件瞬间完成时速度跳变；采样间隔小于 200 ms 时复用上次结果。
- 可重试异常（连接超时、HTTP 5xx/429、IO、校验失败）进入 `Retrying`，并带上第几次尝试与原因。
- 暂停时在读写之间上报 `IsPaused`；恢复后下一次进度回到正常。
- 用户取消 → `Cancelled`；HTTP 超时（同样是 `TaskCanceledException`）按 `Network` 归因，不误报取消。
- 聚合失败使用 `DownloadException.Kind`，UI 显示“网络 / 上游 / 文件校验 / 本地磁盘”等归因。

旧的 `IProgress<DownloadProgress>` 仍保留，并扩展了 `CurrentFileBytes`、`CurrentFileTotalBytes`、
`RetryAttempt`、`RetryReason`、`IsTotalBytesKnown`，现有安装器/整合包进度映射无需重写。

## 4. 任务中心 UI

`OperationCenterViewModel` 订阅 `IOperationStore.Changed`，在主窗口底部显示：

```text
当前任务
client.jar                                            进行中   [取消] [详情]
正在下载文件 libraries/foo.jar
[███░░░░░░░] 32% · 12.6 MB / 39.4 MB    4.2 MB/s    剩余 6 秒
最近任务
example.zip                                            失败     [重试] [详情]
失败原因：文件校验（SHA1 mismatch）
```

- “当前任务”显示未结束任务（最多 2 个），“最近任务”保留最近 1 个已结束任务 2 分钟。
- 阶段按 `StageId` 本地化（`operations.stage.*`），不在下载层硬编码中文文案。
- 下载中、重试中、停滞、失败分别显示不同提示；失败带归因。
- “详情”展开来源、阶段、错误归因/代码/内部详情与更新时间。
- “取消”通过 `IOperationControl.TryCancel()` 转交给任务提供者；“重试”通过
  `IOperationControl.Retry` 让提供者重新执行任务（当前下载批次可用，且只允许启动一次）。
- 取消、重试与诊断都走稳定接口：UI 不解析异常文本，也不直接引用 `DownloadService`。

## 5. 测试

`tests/ECL.Tests/Operations/`：

- 任务生命周期（创建、进度、完成、失败、取消、释放即取消）。
- 暂停与状态机分离；重试后回到运行中。
- 停滞标记但不失败（短间隔计时器）。
- 字节进度、项目进度、ETA；总大小未知时不伪装百分比。
- 速度平滑、节流、无增量时不制造速度。
- 错误归因（HTTP 状态、IO、校验、聚合异常）。
- 取消/重试控制：只有失败任务可重试，重试只启动一次；已结束任务拒绝绑定控制。
- 并发更新与清理。

`tests/ECL.Tests/Download/DownloadServiceTests.cs` 额外覆盖：批次任务发布、字节与来源、
重试快照、未知大小 indeterminate、取消归因、空批次不创建任务、跳过已缓存文件、
暂停/恢复、Range 被忽略、大小不符归因、通过任务中心取消与重试下载。

本阶段本地验证（Windows，.NET SDK 10.0.403）：`dotnet build ECL.sln -c Release` 与
`dotnet format --verify-no-changes` 通过，`dotnet test -c Release` 338 项全部通过
（其中 `Ecl.Tests.Operations` 30 项、下载相关 26 项）。Windows / Linux CI：
[run #169](https://github.com/hellokug2699/Ender-Craft-Launcher/actions/runs/41185715819)
`completed` / `success`（提交 `fe0f9dc`，包含模型/下载/UI/控制与诊断）。

## 6. 已知限制与后续

- 安装器、整合包导入、Java 安装、备份/恢复尚未登记 Operation；它们仍通过各自的 `IProgress<T>` 更新 UI。
- 取消与重试目前由下载批次提供；安装类任务需要各自注册 `IOperationControl` 后才能支持。
- 没有持久化；进程退出后任务记录丢失（接口预留，可替换为持久化 Store）。
- 并发下载时“当前文件”显示最后一个上报的文件，尚未提供整体/单文件/worker 三段式面板。
- CLI 尚未消费任务中心；`ecl` 仍只输出阶段文本。
- 停滞阈值 20 秒、检查周期 2 秒，尚未做成设置项。

## 7. 接入示例

```csharp
using var handle = operations.Begin(new OperationStart
{
    Kind = OperationKind.Backup,
    Title = instance.Name,
    StageId = "backup.pack",
    Total = files.Count,
    TotalBytes = totalBytes,
});

handle.Report(new OperationProgressUpdate
{
    CurrentItem = "saves/",
    Completed = 12,
    BytesDownloaded = copied,
    BytesPerSecond = speed,
});

handle.Complete();
```

失败时使用 `handle.Fail(OperationErrors.FromException(code, exception))`，不要只传 `Exception.Message`。
