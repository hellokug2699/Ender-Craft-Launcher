# 架构决策记录（ADR）

本目录记录 ECL 的正式架构决策。ADR 应使用中文撰写；代码标识、协议字段和状态值可保留 English。

## 状态定义

| Status | 中文含义 | 使用条件 |
|---|---|---|
| `Proposed` | 提案中 | 正在讨论，尚未批准 |
| `Accepted` | 已批准未开始 | 决策已批准，但尚未开始实质实施 |
| `In Progress` | 正在进行 | 决策已批准，实施或验收尚未完成 |
| `Completed` | 已完成 | 决策已落地，当前实现符合 ADR 的验收要求 |
| `Superseded` | 已被取代 | 已由后续 ADR 或明确的新决策取代 |
| `Rejected` | 已拒绝 | 已评审并拒绝，不应实施 |
| `Deprecated` | 已废弃 | 不再推荐采用，但没有单一后续 ADR 完全取代 |

状态描述的是**决策及其落地生命周期**，不是 PR 是否合并，也不是文档是否写完。

- 从 `Proposed` 到 `Accepted`：决策获得批准。
- 从 `Accepted` 到 `In Progress`：开始实质实施。
- 从 `In Progress` 到 `Completed`：验收标准已满足。
- 任意有效状态都可能转为 `Superseded`、`Rejected` 或 `Deprecated`。
- 被取代的 ADR 不删除；必须在正文说明取代它的 ADR，并链接到新决策。
- 新 ADR 部分取代旧 ADR 时，旧 ADR 应说明被取代的具体范围；若旧决策仍有独立有效内容，不得错误地将整份 ADR 标为 `Superseded`。

## 统一文件头

所有 ADR 使用以下字段与顺序：

```markdown
# ADR NNNN：中文标题

- Status: Proposed
- Date: YYYY-MM-DD
- Scope: 适用范围
- Supersedes: [ADR NNNN](NNNN-example.md)（没有则省略）
- Superseded by: [ADR NNNN](NNNN-example.md)（没有则省略）

## 背景

## 决策

## 后果

## 验收标准
```

规则：

1. 编号使用四位数字，按创建顺序递增，不重复、不回收。
2. 标题、背景、决策和说明性正文使用中文；技术名词、代码、命令、协议字段和状态值保留原文。
3. `Date` 是首次形成该决策的日期；修订历史写在正文的“修订记录”中，不通过修改首次日期伪造新 ADR。
4. `Scope` 应简明描述受影响的模块或边界。
5. `Supersedes` 和 `Superseded by` 仅在存在关系时添加，使用相对 Markdown 链接。
6. 已批准的 ADR 应包含可验证的验收标准；纯设计探索应保持 `Proposed`。
7. 实现状态不确定时使用较保守的状态，不得仅凭代码存在就标记 `Completed`。
8. ADR 只记录正式架构决策、理由、约束、后果和验收标准；不在 ADR 正文中记录私有实现文档的位置。

## 当前状态概览

状态应以各 ADR 文件头部为准。本表仅作为目录导航，更新 ADR 状态时应同步更新本表。

| ADR | Status | 范围 |
|---|---|---|
| [0015 按进程隔离游戏日志会话与诊断证据](0015-process-scoped-game-log-sessions-and-diagnostics.md) | Completed | 日志会话、崩溃证据与诊断导出 |
| [0016 独立 Bootstrapper 与安全部署边界](0016-independent-bootstrapper-and-safe-deployment-boundaries.md) | Completed | 部署、更新、回滚与用户数据边界 |
| [0017 图形化首次安装程序](0017-graphical-first-run-installer.md) | Completed | 首次安装 GUI 与 Bootstrapper 部署边界 |
| [0018 Minecraft 工作区与实例存储位置](0018-minecraft-workspace-and-instance-storage.md) | Completed | 工作区选择、外部实例与迁移数据保护 |
| [0019 内存压力下的进程保活与诊断连续性](0019-memory-pressure-process-survival-and-diagnostic-continuity.md) | Completed | 游戏、LogHost 与主 UI 的资源压力生存边界 |
| [0020 下载临时产物生命周期与失败清理](0020-download-artifact-lifecycle-and-cleanup.md) | Accepted | 下载缓存、断点续传与临时文件生命周期 |
