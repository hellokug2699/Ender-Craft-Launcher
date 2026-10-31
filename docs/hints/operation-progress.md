# Operation / Progress / Diagnostics UX — Implementation Hints

> 给实现模型的施工提示。本文不是最终 API 设计，也不是要求一次性完成全部功能；目标是建立可扩展的统一任务进度基础设施，避免在各业务服务里继续堆临时 `IProgress<T>`。

## 1. 先理解现状，再改架构

当前下载层已经存在基础进度能力：`DownloadService.DownloadAsync(...)` 接收 `IProgress<DownloadProgress>`，`DownloadProgress` 已经包含文件数、字节数、当前文件、速度和暂停状态。不要推倒重来。

当前实现还存在几个明显限制：

- 进度主要停留在“下载批次”层，没有统一的业务操作模型。
- `BytesPerSecond` 是批次累计值，不能准确表达当前文件/并发任务的瞬时速度。
- 下载循环没有持续报告每次 read/write 的进度，因此 UI 无法稳定显示实时百分比、速度和 ETA。
- Modpack、Content、Vanilla Install 等服务通过 `ForwardingProgress<T>` 将下载进度转换成自己的 Progress 类型，容易形成越来越多的局部模型。
- 下载失败信息主要在下载服务内部聚合，UI 不容易判断“网络 / 上游 / 校验 / ECL 内部”究竟是哪一层出错。

因此本次工作的目标不是简单“给下载页面加一个 ProgressBar”，而是建立统一的 Operation 模型，同时保持现有下载 API 可以平滑迁移。

## 2. 目标架构

推荐目标链路：

```text
Business Service
    ↓
Operation / OperationContext
    ↓
Progress + Stage + Diagnostics
    ↓
Operation Store / Event Stream
    ↓
UI / CLI / Logging
```

Operation 应该成为 ECL 中所有长时间任务的统一抽象，而不是只服务于 DownloadService。

至少考虑这些任务：

- Minecraft 安装
- Mod / Modpack 安装
- Java 下载与安装
- 文件下载
- 文件校验
- 实例更新
- 导入 / 导出整合包
- 备份 / 恢复
- Minecraft 启动
- 后续的资源/内容同步

## 3. Operation 状态

建议使用有限且稳定的状态集合：

```text
Pending
Running
Retrying
Completed
Failed
Cancelled
```

不要让 UI 依赖大量布尔值，例如 `IsDownloading && !IsFailed && !IsPaused ...`。状态应当有明确的生命周期。

暂停最好作为独立属性，而不是把 `Paused` 混进主状态机；因为“暂停”描述的是运行控制，而不是任务最终结果。

## 4. Operation 数据模型

建议至少支持以下信息：

```text
OperationId
Title
State
Stage
CurrentItem
Completed
Total
BytesDownloaded
TotalBytes
BytesPerSecond
Eta
Source
Error
StartedAt
UpdatedAt
CanCancel
CanRetry
```

注意：

- `Completed / Total` 可以表达文件、项目或步骤数量。
- `BytesDownloaded / TotalBytes` 用于字节级进度。
- `TotalBytes` 不可得时必须允许 indeterminate。
- `BytesPerSecond` 没有足够样本时可以为 0/unknown，不要制造虚假的速度。
- ETA 在总大小未知或速度不足时应为 unknown。
- `Source` 应该能表达 Modrinth、CurseForge、Wikifactory、Mojang 或其他来源。
- Error 不要只保存 `Exception.Message`；至少保留可分类的错误类型/原因。

## 5. Stage 比百分比更重要

UI 不应该只有：

```text
[██████████------] 67%
```

必须能够告诉用户当前正在做什么，例如：

```text
正在解析 Minecraft 1.21.1
正在下载 client.jar
正在下载 libraries/xxx.jar
正在校验文件
正在安装 Fabric
正在启动 Minecraft
```

百分比无法解释“为什么 67% 卡了两分钟”，Stage 可以。

因此 Operation 应支持稳定的 Stage 标识和用户可本地化的显示文本。不要把大量中文 UI 文本硬编码在底层 DownloadService。

## 6. 下载进度必须区分两个层次

并发下载时至少存在：

```text
Overall
  1.82 GB / 2.71 GB   67%
  12.8 MB/s            ETA 1m 08s

Current item
  libraries/foo.jar
  18.2 MB / 21.4 MB    85%
  9.3 MB/s

Workers
  8 / 12
```

不要用“已完成文件数量 / 总文件数量”冒充整体字节进度。

`DownloadService` 当前在文件完成时才更新 `CompletedBytes`，这是可以作为兼容层保留的旧行为，但新 Operation 层需要在流式读取过程中获得增量 byte progress。

## 7. 速度计算

不要简单用：

```text
completedBytes / processLifetime
```

作为长期显示速度。

建议使用滑动时间窗口或指数平滑：

```text
instantSpeed = deltaBytes / deltaTime
smoothedSpeed = smoothing(instantSpeed)
```

这样速度不会因为某一个大文件瞬间完成而跳成离谱的数字。

实现时注意：

- 不要每读几十 KB 就触发一次 UI 更新。
- 可以按时间间隔或字节阈值节流，例如约 100–250ms 一次。
- UI 更新频率与实际下载采样频率可以不同。

## 8. “卡住”与“失败”必须区分

这是 UX 的关键。

```text
0 B/s ≠ 一定失败
```

例如 HTTP 连接仍然存在、服务器暂时没有返回数据时，不应该立即显示红色失败。

建议至少存在：

```text
Running + recent activity
Running + stalled
Retrying
Failed
```

Stalled 可以作为诊断状态/标志，而不是立即结束 Operation。

UI 可以显示：

> 下载似乎已暂停响应（已等待 18 秒）
> [继续等待] [重试] [取消]

超时策略仍由 DownloadService 决定；Operation 层负责把这个事实解释给用户。

## 9. 错误必须能够归因

至少区分：

```text
Network
Upstream
Authentication
FileIntegrity
LocalIO
Configuration
EclInternal
Cancelled
```

示例：

```text
Network
DNS resolution failed

Upstream
CurseForge returned HTTP 503

FileIntegrity
SHA1 mismatch

LocalIO
No space left on device
```

不要把所有错误最后都变成：

```text
下载失败：xxx
```

用户需要知道“是谁的问题”。

## 10. Retry 必须是 Operation 的一部分

现在 DownloadService 已经有 URL fallback 和 attempt retry。不要在 UI 再实现一套独立 retry 逻辑。

推荐：

```text
Operation
  ↓
DownloadService
  ↓
retry attempt
  ↓
Operation.Progress / Diagnostics
```

UI 的“重试”应该重新触发 Operation，而不是偷偷修改底层 HTTP 请求。

Retrying 时应该能告诉用户：

```text
正在重试（2/3）
来源：Modrinth
原因：HTTP 503
```

## 11. Cancellation / Pause

Cancellation 和 Pause 不要混为一谈：

- Cancel：终止 Operation。
- Pause：暂停可暂停的工作，Operation 仍然存在。
- Retry：在失败后创建/恢复新的执行尝试。

当前 `IDownloadController` 已提供全局暂停/恢复能力，可以保留并逐步把它纳入 Operation 控制模型，不要突然删除旧 API。

## 12. 不要让 UI 直接依赖 DownloadService

推荐：

```text
ViewModel
   ↓
IOperationService / IOperationStore
   ↓
Operation
   ↓
IDownloadService
```

而不是：

```text
ViewModel → DownloadService → IProgress<DownloadProgress>
```

这样未来 Java 安装、Modpack 导入、启动游戏都可以进入同一个 Operation Center。

## 13. Operation Store

建议提供一个进程内的 Operation Store，负责：

- 注册 Operation
- 更新 Operation snapshot
- 查询当前运行任务
- 查询最近完成/失败任务
- 订阅变更
- 根据 OperationId 获取详情

不要一开始就把 Operation Store 做成数据库。第一阶段内存实现即可。

但接口应该允许以后替换为持久化实现。

## 14. Event / Snapshot

UI 更适合消费 snapshot，而底层适合产生事件。

可以采用：

```text
OperationStarted
OperationProgressChanged
OperationStageChanged
OperationRetrying
OperationCompleted
OperationFailed
OperationCancelled
```

然后 Store 维护最新 snapshot。

不要让 UI 必须自己重放全部事件才能知道当前状态。

## 15. 对现有 DownloadService 的最小侵入改造

第一阶段不要重写整个 DownloadService。

优先做：

1. 扩展 `DownloadProgress`，让它能够表达当前文件的字节进度。
2. 在 `DownloadFromUrlAsync` 的流读取循环中产生增量进度。
3. 为速度计算加入节流/平滑。
4. 将已有 batch progress 映射到 Operation snapshot。
5. 保留 `IProgress<DownloadProgress>`，让现有调用方不必一次性全部改完。
6. 后续逐步迁移 Modpack / Content / Install service。

也就是说：

```text
旧 API
  ↓ adapter
新 Operation
```

而不是：

```text
旧 API 全删
  ↓
全仓库同时重构
```

后者非常容易制造巨大返工。

## 16. 第一阶段不要做的事情

本阶段不要顺手做：

- Cloudflare Download Gateway
- R2 cache
- Mainland CDN
- 全新的下载协议
- 数据库持久化 Operation
- 全新 UI 框架
- 全面重写 DownloadService
- 大规模重命名已有 API
- 修改 README 分支策略

这些都是独立工作项。

当前目标只有：

> 让 ECL 第一次拥有统一、可靠、可被 UI/CLI 消费的任务进度与诊断基础设施。

## 17. 推荐实施顺序

### P0-1：模型

建立：

```text
OperationState
OperationStage
OperationSnapshot
OperationError
```

并定义稳定的接口：

```text
IOperationService
IOperationStore
```

### P0-2：下载适配

把现有 `DownloadProgress` 接入 Operation。

必须支持：

- overall bytes
- current file bytes
- speed
- ETA
- retrying
- paused
- source
- cancellation

### P0-3：实时流式进度

改造 `DownloadFromUrlAsync`，在 stream read loop 中产生增量 bytes。

不要等待整个文件完成后才更新进度。

### P0-4：UI 最小闭环

先做一个最小任务显示：

```text
正在下载 Minecraft
67%
12.8 MB/s
ETA 1:08
正在下载 client.jar
```

先验证数据链路正确，再做漂亮 UI。

### P1：Operation Center

支持多个并发 Operation：

```text
当前任务
  Minecraft 安装       72%  12.8 MB/s
  Modpack 下载          41%   5.2 MB/s

最近任务
  ✓ Java 安装
  ✕ Mod 下载失败
```

### P1：诊断

让失败结果能够明确归因，并提供 Retry / Cancel / Open details。

## 18. 测试要求

至少覆盖：

- 空下载列表
- 单文件下载
- 多文件并发下载
- 已缓存文件跳过
- 断点续传
- HTTP 206
- HTTP 200 覆盖 Range 请求
- Retry
- Cancellation
- Pause / Resume
- Hash mismatch
- Size mismatch
- Unknown total size
- Speed calculation
- Stalled detection
- Operation lifecycle
- Concurrent Operation store updates

尤其要测试：

```text
取消 ≠ 失败
暂停 ≠ 完成
Retrying ≠ Failed
Stalled ≠ Failed
```

## 19. 实现纪律

- 先搜索现有 abstraction，再创建新 abstraction。
- 能 adapter 就不要复制一套服务。
- 底层库不应该知道 WPF 控件。
- UI 不应该解析 Exception.Message 来判断错误类型。
- 不要在 DownloadService 中加入大量 UI 文案。
- 不要为了进度系统顺手修改无关模块。
- 每一个阶段都应该能独立编译和测试。
- 优先小 PR，而不是一次巨大重构。

## 20. 完成标准

本任务不是“ProgressBar 出现了”就算完成。

最终至少应该达到：

```text
用户可以回答：

1. ECL 现在在干什么？
2. 已经完成多少？
3. 现在下载多快？
4. 预计还要多久？
5. 当前正在处理哪个文件？
6. 如果卡住了，是网络、上游、文件还是 ECL？
7. 可以等待、重试还是取消？
8. 如果同时有多个任务，每个任务分别怎么样？
```

如果用户无法回答这些问题，说明 Operation / Progress / Diagnostics 链路还没有真正完成。
