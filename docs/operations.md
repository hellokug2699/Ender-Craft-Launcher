# 任务进度与诊断（Operation）

Operation 是 ECL 长时间任务的统一进度与诊断模型。当前实现覆盖**下载批次、Minecraft 安装、
整合包导入、内容安装/下载、CurseForge Mod 安装与 Java 运行时下载**；备份/恢复、整合包导出、
启动、实例迁移与删除等任务尚未接入，仍使用各自的 `IProgress<T>` 或阶段报告。

WPF 任务中心与 CLI 都只消费 `IOperationStore` 快照，不直接依赖具体业务服务，也不解析异常文本判断错误类型。

## 1. 当前范围

| 任务 | 入口 | `OperationKind` | 已接入 | 取消 / 重试 |
| --- | --- | --- | --- | --- |
| 下载批次 | `IDownloadService.DownloadAsync` | `Download` | 是 | 是 |
| Minecraft 安装 / 原地重配置 | `IVersionInstallService.InstallAsync` / `ReconfigureAsync` | `Install` | 是 | 是 |
| 整合包导入 | `IModpackService.ImportAsync` | `Import` | 是 | 是 |
| 内容安装 / 仅下载 / 整合包导入 | `IContentInstallService` 的四个入口 | `Install` / `Download` / `Import` | 是 | 是 |
| CurseForge Mod 安装 | `ICurseForgeService.InstallAsync` | `Install` | 是 | 是 |
| Java 运行时下载 | `IJavaInstallService.DownloadAsync`（含 `EnsureAsync`） | `Java` | 是 | 是 |
| 启动、备份/恢复、整合包导出、迁移、删除、诊断包导出 | — | — | 否 | 否 |

边界说明：

- “已接入”指任务在任务中心登记快照、上报阶段/进度，并可被 UI 或 CLI 读取；不代表对应业务链路已经完成实机验收。
- 未接入的任务不会出现在任务中心，也不会出现在 CLI 的进度输出中。
- 旧的 `IProgress<T>` 参数全部保留；调用方可以继续只使用业务进度类型。

## 2. 分层与依赖

```text
Business Service（下载 / 安装 / 导入 / 内容 / CurseForge / Java）
        ↓ OperationProgressBridge<T>
IOperationService（写入端） → OperationScope（当前任务作用域）
        ↓
IOperationStore（内存快照 + Changed 事件，读取端）
        ↓
OperationCenterViewModel（WPF） / CliOperationReporter（CLI）
```

- `ECL.Operations` 不依赖 Core、Download 或 WPF，可被任意业务模块引用。
- `ECL.Download` 在 `AddEclDownload()` 中调用 `AddEclOperations()`，因此所有注册了下载模块的宿主（App、CLI、测试）都会得到同一个进程内任务中心。
- `ECL.Minecraft`、`ECL.Java`、`ECL.App`、`ECL.Cli` 显式引用 `ECL.Operations`。
- `IOperationService` 与 `IOperationStore` 解析到同一个 `OperationService` 实例；UI 只注入读取端，写入端留给业务服务。

## 3. 模型

### 状态

```text
Pending → Running ⇄ Retrying → Completed / Failed / Cancelled
```

- `Pending` 预留给排队任务；当前实现的登记即进入 `Running`。
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
- `OperationFormat` 提供 UI 与 CLI 共用的字节/速度格式化，避免两处显示不一致。

### 阶段标识

每个业务任务使用稳定的 `StageId` 与可本地化的 `StageText`：

| 任务 | `StageId` |
| --- | --- |
| 下载 | `download` |
| 安装 | `install.manifest`、`install.version`、`install.loader`、`install.processors`、`install.client`、`install.libraries`、`install.assets`、`install.logging`、`install.fabric-api`、`install.optifine`、`install.completed` |
| 整合包导入 | `modpack.resolve`、`modpack.download`、`modpack.overrides` |
| 内容安装/下载 | `content.resolve`、`content.resolveCurseForge`、`content.download`、`content.inspect`、`content.import` |
| CurseForge Mod | `curseforge.resolve`、`curseforge.download`、`curseforge.done` |
| Java | `java.index`、`java.manifest`、`java.provider`、`java.download`、`java.extract`、`java.files`、`java.completed` |

`StageText` 由业务服务通过 `ILocalizer` 生成。UI 先查 `operations.stage.<stageid>` 本地化键，
找不到时直接显示 `StageText`；因此新增业务阶段只需要提供已本地化的文本，不必再补一整套阶段键。

### 进度语义

- 下载批次：`BytesDownloaded/TotalBytes` 是整批字节，`CurrentItem` 是最近上报的文件；总大小未知时不显示百分比。
- 安装：`Completed/Total` 与字节数**按当前阶段**统计，阶段切换会重新开始计数；阶段文本说明正在做什么，
  不把多个阶段的字节拼成一个不可解释的总量。
- 整合包导入 / 内容安装 / CurseForge / Java：下载阶段使用批次的字节与项目数量，非下载阶段只上报阶段与项目数。

## 4. 嵌套任务规则

一次安装内部会触发多个下载批次、整合包导入和内容下载。旧行为会让每个下载批次都登记一个任务，
一次安装因此产生十几个“当前任务”条目。当前实现用 `OperationScope`（`AsyncLocal`）解决：

```csharp
using var operation = new OperationProgressBridge<InstallProgress>(
    operations,
    new OperationStart { Kind = OperationKind.Install, Title = "安装 Survival" },
    progress,                     // 原有 IProgress<T>，继续转发
    MapInstallProgress);          // 业务进度 → OperationProgressUpdate
```

- `OperationProgressBridge<T>` 在**没有上层任务**时登记 Operation 并进入作用域；
- 作用域内的子服务（下载批次、`IModpackService`、`ICurseForgeService`）检测到已有上层任务，
  只转发进度，不再登记重复任务；
- 子服务不拥有任务生命周期：完成、失败、取消仍由最外层业务服务决定；
- 下载批次仍可把内部重试（`Retrying`）上报到上层任务，避免用户误以为任务卡住；
- 未注册 `IOperationService` 时，桥接退化为普通进度转发，业务服务行为不变。

## 5. 错误归因

`OperationErrorKind`：`Network`、`Upstream`、`Authentication`、`FileIntegrity`、`LocalIO`、
`Configuration`、`EclInternal`、`Cancelled`。

两种映射方式：

- `OperationErrors.FromException(code, exception)`：按异常类型/HTTP 状态码分类，用于业务服务内部抛出的异常。
- `OperationErrors.FromCode(code, message, detail)`：按稳定错误码关键词推断（下载 → 网络、哈希/大小 → 文件校验、
  未找到/不支持/不匹配 → 配置、CurseForge/Modrinth/HTTP → 上游等），用于业务服务返回的 `Result` 失败。

下载失败优先使用 `DownloadException.Kind`，它是聚合多个文件失败后的主要归因。
`OperationErrors.Worst` 在多种失败并存时选择最需要用户处理的原因（认证 > 配置 > 校验 > 本地磁盘 > 网络 > 上游）。

## 6. 取消与重试

`IOperationControl` 由任务提供者实现：

- `TryCancel()`：触发该任务自己的取消源；`OperationControl` 是通用实现。
- `Retry`：重新执行同一业务动作的工厂；未提供时为 `null`。

接入方式：业务服务创建链接到调用方令牌的 `CancellationTokenSource`，通过
`bridge.AttachControl(cancellation, retryToken => 公开入口(..., retryToken))` 注册。

- 取消只是终止本次执行，业务服务仍按自己的语义返回 `Result` 或抛出 `OperationCanceledException`；
  Operation 层把 `Cancelled` 与 `Failed` 分开，取消不算失败。
- 重试会生成**新的** Operation，旧失败记录保留；只允许对失败任务重试一次，UI 不复制底层 HTTP 重试。
- 下载批次仍保留每 URL 最多 3 次尝试与候选源回退；内部重试通过 `Retrying` 上报，而不是让任务直接失败。

## 7. 各业务接入详情

### 7.1 下载

`DownloadAsync` 每个批次自动登记一个 `Download` 任务：

- 标题取第一个文件的显示名（多文件追加 `(+N)`），来源取下载 URL 的主机名。
- 流式读取时按约 150 ms 节流上报当前文件已写字节，不等整个文件完成。
- `SpeedMeter` 使用指数平滑（EMA），采样间隔小于 200 ms 时复用上次结果。
- 可重试异常（连接超时、HTTP 5xx/429、IO、校验失败）进入 `Retrying`，并带上第几次尝试与原因。
- 暂停时在读写之间上报 `IsPaused`；恢复后下一次进度回到正常。
- 用户取消 → `Cancelled`；HTTP 超时（同样是 `TaskCanceledException`）按 `Network` 归因，不误报取消。
- 聚合失败使用 `DownloadException.Kind`，UI 显示“网络 / 上游 / 文件校验 / 本地磁盘”等归因。
- 存在上层业务任务时不再登记自己的任务，进度通过 `IProgress<DownloadProgress>` 转发。

旧 `IProgress<DownloadProgress>` 仍保留，并包含 `CurrentFileBytes`、`CurrentFileTotalBytes`、
`RetryAttempt`、`RetryReason`、`IsTotalBytesKnown`。

### 7.2 安装

`IVersionInstallService.InstallAsync` 在实例与加载器校验通过后登记 `Install` 任务：

- 标题为 `install.task.title`（“安装 {实例名}”），来源为 `Mojang`。
- 整个安装（清单 → 版本 → 加载器 → processors → client → 依赖库 → 资源 → 日志 → 组件）只登记一个任务。
- 每次阶段切换上报 `StageId` 与本地化阶段文本；下载阶段同时带项目数、字节、速度与当前文件。
- 成功 → `Completed`；返回失败 → `Failed`（错误码原样保留，归因由 `OperationErrors.FromCode` 推断）。
- 下载异常按 `DownloadException.Kind` 归因；用户取消 → `Cancelled`；其他已处理异常 → `EclInternal` / 具体类型归因。
- 取消触发内部取消源；安装事务回滚与诊断写入行为不变。
- `ReconfigureAsync` 通过 `InstallAsync` 执行，因此同样出现在任务中心。

### 7.3 整合包导入

`IModpackService.ImportAsync` 登记 `Import` 任务：

- 标题为归档文件名；来源在解析前按扩展名推断（`.mrpack` → `Modrinth`，其他 → `CurseForge`）。
- 阶段为下载 → 覆盖文件；CurseForge 整合包在下载前多一个 `modpack.resolve`（批量解析文件元数据）。
- 下载阶段转发字节、速度与当前文件。
- 阶段文案已改为本地化键（`modpack.stage.*`），不再在服务里硬编码中文。
- 业务失败（例如游戏版本不匹配）会以原错误码显示在任务中心，归因为 `Configuration`。

### 7.4 内容安装 / 下载

`IContentInstallService` 的四个公开入口分别登记任务：

- `InstallAsync`：整合包 → `Import`，其余 → `Install`；标题为条目名称，来源为 `CurseForge` / `Modrinth`。
- `DownloadAsync`：`Download`，仅下载到启动器下载目录。
- `ImportModpackAsync` / `CreateInstanceAndImportModpackAsync`：`Import`。
- 内部调用（内容安装 → 整合包导入 → 下载）复用同一个任务；阶段为解析、下载、读取元数据、导入。
- CurseForge Mod 路径复用 `ICurseForgeService`，其进度通过 `CurseForgeProgress` 转发到当前任务。
- 失败时保留业务错误码；取消后新建实例的回滚逻辑不变。

### 7.5 CurseForge Mod 安装

`ICurseForgeService.InstallAsync` 登记 `Install` 任务：标题为用户输入的引用（项目 ID / slug / 链接），
来源为 `CurseForge`；阶段为解析项目 → 下载文件 → 完成，下载阶段转发字节、速度与当前文件；
阶段文案使用 `curseforge.stage.*` 本地化键。

### 7.6 Java 运行时下载

`IJavaInstallService.DownloadAsync` 登记 `Java` 任务：

- 标题为 `java.task.title`（“Java {主版本} 运行时”）；来源为所选提供方名称，Mojang 运行时显示 `Mojang`。
- 阶段为索引 → 清单 → 提供方 → 下载 → 解压 → 文件 → 完成；下载阶段带字节、速度与当前文件。
- `EnsureAsync` 在已找到兼容 Java 时直接返回，不登记任务；只有真正下载时才出现任务。
- 业务失败保留错误码，例如 `java.provider_unsupported` 归因为配置。

## 8. WPF 任务中心

`OperationCenterViewModel` 订阅 `IOperationStore.Changed`，在主窗口底部显示：

```text
当前任务
安装 Survival                                          进行中   [取消] [详情]
正在下载依赖库…
[██████░░░░] 58% · 128.4 MB / 220.9 MB    4.2 MB/s    剩余 22 秒
最近任务
导入 example.mrpack                                    失败     [重试] [详情]
失败原因：文件校验（哈希校验失败。）
```

- “当前任务”显示未结束任务（最多 2 个），“最近任务”保留最近 1 个已结束任务 2 分钟。
- 阶段按 `operations.stage.*` 查询，未命中时使用服务提供的 `StageText`。
- 下载中、重试中、停滞、失败分别显示不同提示；失败带归因。
- “详情”展开来源、阶段、错误归因/代码/内部详情与更新时间。
- “取消”通过 `IOperationControl.TryCancel()` 转交给任务提供者；“重试”通过 `IOperationControl.Retry`
  让提供者重新执行任务（只允许启动一次）。
- UI 不解析异常文本，也不直接引用 `DownloadService`、`IModpackService` 等业务服务。

## 9. CLI 输出

`CliOperationReporter` 在 `ecl install`、`ecl java install`、`ecl modpack import` 与
`ecl curseforge install` 期间订阅 `IOperationStore`：

- 阶段变化立即输出一行；纯进度（百分比、速度、ETA）每秒刷新一次，避免刷屏。
- 重试与失败会输出“正在重试（2/3）”与“失败原因：网络（…）”。
- 进度写入**标准错误**，`--json` 的标准输出仍是纯净 JSON。
- 其余命令（启动、搜索、列表等）保持原有输出。

## 10. 测试与验证

`tests/ECL.Tests/Operations/`：

- 任务生命周期（创建、进度、完成、失败、取消、释放即取消）、暂停与状态机分离、重试后回到运行中。
- 停滞标记但不失败；字节进度、项目进度、ETA；总大小未知时不伪装百分比。
- 速度平滑、节流、无增量时不制造速度；错误归因（异常类型、HTTP 状态码、业务错误码）。
- 取消/重试控制：只有失败任务可重试，重试只启动一次；已结束任务拒绝绑定控制。
- 进度桥接：同时转发给旧 `IProgress<T>` 与任务中心；嵌套桥接复用上层任务；作用域进入/退出；
  未注册任务中心时退化为普通转发。
- 并发更新与清理。

`tests/ECL.Tests/Download/DownloadServiceTests.cs` 额外覆盖：批次任务发布、字节与来源、重试快照、
未知大小 indeterminate、取消归因、空批次不创建任务、跳过已缓存文件、暂停/恢复、Range 被忽略、
大小不符归因、通过任务中心取消与重试下载，以及**嵌套业务任务只保留一个任务**。

业务接入用例（分别位于 `Install`、`Minecraft`、`Modrinth`、`CurseForge`、`Java`、`Cli` 测试目录）
覆盖：安装只登记一个 `Install` 任务并完成、安装失败保留错误码与归因、整合包导入成功/失败、
内容安装的嵌套任务合并、CurseForge 安装完成阶段、Java 下载完成阶段，以及 CLI 的阶段/重试/失败输出和
“只跟踪第一个运行中任务”的行为。

**本地验证（2026-10-31，Windows，.NET SDK 10.0.403）：**

```text
dotnet build ECL.sln -c Release          → 0 warning / 0 error
dotnet format ECL.sln --verify-no-changes → 无差异
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release → 360 项全部通过
```

分类数量：`Ecl.Tests.Operations` 48、下载 27、安装 14、Modrinth/内容 13、Java 60、CurseForge 10、CLI 2。
该记录只证明本次本地执行结果；本次变更尚未附带对应提交的 CI 运行结果。

**历史记录：** 上一次文档提到的 CI run #169（提交 `fe0f9dc`）属于第一阶段的记录，
不代表当前分支 HEAD 的验证结果。

## 11. 已知限制与后续

- 备份/恢复、整合包导出、启动、实例迁移、删除与诊断包导出尚未登记 Operation；它们仍使用各自的阶段报告。
- 没有持久化：进程退出后任务记录丢失；接口预留，可替换为持久化 Store。
- CLI 只输出当前任务进度，没有可交互的任务列表；“继续等待”之类的控制仍由 WPF 任务中心提供。
- 并发下载时“当前文件”显示最后一个上报的文件，尚未提供整体/单文件/worker 三段式面板。
- 停滞阈值 20 秒、检查周期 2 秒，尚未做成设置项。
- 只广播单一 `Changed` 事件，未拆分 `Started` / `StageChanged` / `Retrying` 等细粒度事件。
- 安装任务按阶段统计项目数与字节；如果将来需要“整个安装的累计字节”，需要额外的全局总量来源，而不是把阶段值相加。

## 12. 接入示例

```csharp
using var operation = new OperationProgressBridge<MyProgress>(
    operations,                       // IOperationService?；为 null 时自动退化为纯转发
    new OperationStart
    {
        Kind = OperationKind.Backup,
        Title = instance.Name,
        StageId = "backup.pack",
        Source = "Local",
        CanCancel = true,
        CanRetry = true,
    },
    progress,                         // 原有 IProgress<MyProgress>?
    value => new OperationProgressUpdate
    {
        StageId = "backup.pack",
        StageText = value.Message,
        CurrentItem = value.CurrentFile,
        Completed = value.Completed,
        Total = value.Total,
        BytesDownloaded = value.CompletedBytes,
        TotalBytes = value.TotalBytes,
        BytesPerSecond = value.BytesPerSecond,
    });

using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
operation.AttachControl(cancellation, retryToken => BackupAsync(instance.Id, progress, retryToken));

try
{
    var result = await BackupCoreAsync(instance, operation, cancellation.Token);
    if (result.IsSuccess)
    {
        operation.Complete();
    }
    else
    {
        operation.Fail(OperationErrors.FromCode(result.Error!.Code, result.Error.Message, result.Error.Detail));
    }

    return result;
}
catch (OperationCanceledException)
{
    operation.Cancel();
    throw;
}
catch (Exception exception)
{
    operation.Fail(OperationErrors.FromException("backup.failed", exception));
    throw;
}
```

失败时不要只传 `Exception.Message`：优先使用 `OperationErrors.FromException`（有异常）或
`OperationErrors.FromCode`（只有 `Result` 错误码），让 UI 能区分网络、上游、校验、本地磁盘与配置问题。
