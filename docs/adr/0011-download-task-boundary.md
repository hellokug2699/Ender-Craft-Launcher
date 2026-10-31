# ADR 0011：全局下载任务与页面解耦

- 状态：已被 `ECL.Operations` 统一任务模型取代

## 背景

当前下载能力已经集中到 `IDownloadService`，但 WPF 下载页仍同时承担搜索、实例安装、Java 下载、整合包导入、进度显示和暂停/继续等应用层协调工作。

这会导致下载生命周期与页面生命周期耦合：离开下载页后，任务状态没有一个明确的应用层事实来源，后续也容易形成 ViewModel → ViewModel 的依赖。

## 后续决策

后续实现没有继续采用本 ADR 提出的 `DownloadTask` / `IDownloadTaskManager` 专用模型，而是采用跨业务的 `ECL.Operations`：

- `IOperationService` / `IOperationHandle`：任务创建、状态和进度写入。
- `IOperationStore`：统一读取内存快照与变更事件。
- 下载服务通过适配器发布 Operation；UI 消费快照，不直接依赖 `DownloadService`。
- 暂停、取消和重试通过 `IOperationControl` 转交给任务提供者。
- 安装、Java、整合包、备份等业务逐步迁移到统一模型，不再另建下载专属任务中心。

当前实际实现与限制以 [operations.md](../operations.md) 为准。

## 非目标

本 ADR 不重写现有 HTTP 下载器，也不在第一阶段改变下载源、重试、续传和校验策略。
