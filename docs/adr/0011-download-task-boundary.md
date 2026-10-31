# ADR 0011：全局下载任务与页面解耦

## 状态

Nightly / Proposed

## 背景

当前下载能力已经集中到 `IDownloadService`，但 WPF 下载页仍同时承担搜索、实例安装、Java 下载、整合包导入、进度显示和暂停/继续等应用层协调工作。

这会导致下载生命周期与页面生命周期耦合：离开下载页后，任务状态没有一个明确的应用层事实来源，后续也容易形成 ViewModel → ViewModel 的依赖。

## 决策

引入 `DownloadTask` 与 `IDownloadTaskManager` 作为应用层下载任务边界。

- `IDownloadService`：负责实际的 HTTP / 文件下载执行。
- `IDownloadTaskManager`：负责任务生命周期、状态、暂停/继续/取消和任务集合。
- ViewModel：只提交任务并观察任务状态，不直接拥有下载生命周期。
- Home、Download、Instance 等页面共享同一任务状态，而不是互相调用 ViewModel。

任务状态固定为：

`Queued → Downloading → Paused → Downloading → Completed`

以及失败 / 取消终态：

`Failed` / `Cancelled`。

## 后续迁移

1. 为现有 `IDownloadController` 建立 `IDownloadTaskManager` 实现。
2. 将 `DownloadViewModel` 的暂停/继续逻辑迁移到任务管理器。
3. 将 Java、游戏安装、Content 安装产生的下载统一登记为任务。
4. 最终禁止 `ViewModel -> ViewModel` 的业务调用。

## 非目标

本 ADR 不重写现有 HTTP 下载器，也不在第一阶段改变下载源、重试、续传和校验策略。
