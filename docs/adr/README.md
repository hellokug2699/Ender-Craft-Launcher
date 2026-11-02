# 架构决策记录（ADR）

本目录记录 ECL 的正式架构决策。ADR 使用中文撰写；代码标识、协议字段和状态值可保留 English。

ADR 不是功能愿望清单，也不是实施任务列表。它记录需要长期保留的架构选择、选择理由、替代方案、约束、代价及验证方式。纯产品构想优先写 Proposal；具体功能行为和验收细节优先写 Feature Spec；实施任务进入 Roadmap / Issue。

## 生命周期模型

ADR 将**决策生命周期**与**实施生命周期**分开记录。批准一项决策不代表已经实现，更不代表已经验证或投入生产。

### Decision Status（决策状态）

| Status | 中文含义 | 使用条件 |
|---|---|---|
| `Proposed` | 提案 / 待评审 | 方案正在讨论，尚未成为 ECL 的正式架构约束。不得据此宣称实现必须遵循该方案。 |
| `Accepted` | 决策已批准 | 已完成评审并决定采用；从此成为架构约束。**不表示已经开始实施、已经实现或已经投入使用。** |
| `Rejected` | 已拒绝 | 经评审决定不采用。保留记录及拒绝理由，避免重复讨论。 |
| `Superseded` | 已被取代 | 后续决策替代了本 ADR 的全部或明确范围。必须链接替代 ADR，并说明取代范围。 |
| `Deprecated` | 已废弃 | 不再推荐采用，但没有单一 ADR 完全取代；说明废弃原因及迁移方向。 |

### Implementation Status（实施状态）

| Status | 中文含义 | 使用条件 |
|---|---|---|
| `Not Started` | 尚未实施 | 决策已批准，但尚无实质实现工作。 |
| `In Progress` | 实施中 | 已开始实现，但范围尚未完成。 |
| `Implemented` | 已实现 | 约定范围已有实现，但验收或验证仍未全部完成。 |
| `Verified` | 已验证 | 已完成约定验收，并有测试、构建、实机验证或其他可复核证据。 |
| `N/A` | 不适用 | 例如纯治理规则、已拒绝提案或不要求代码实施的决策。 |

### 两个状态如何组合

- `Proposed + Not Started`：正在讨论的方案，不构成正式约束。
- `Accepted + Not Started`：已经批准，但尚未开始实施。
- `Accepted + In Progress`：已经批准，正在实施。
- `Accepted + Implemented`：实现已完成，验证尚未全部完成。
- `Accepted + Verified`：实现及约定验收均已完成。
- `Rejected + N/A`：已拒绝，不应实施。
- `Superseded + N/A`：已被后续决策取代，保留历史记录。

`Accepted` 是决策状态，不是发布状态；`Verified` 也不自动代表已经进入 stable 发布频道。是否发布、发布到哪个频道，由发布流程单独决定。

## 状态变更规则

1. 新 ADR 默认从 `Proposed + Not Started` 开始。
2. 只有完成评审并明确批准后，才将 Decision Status 改为 `Accepted`。
3. 开始实质实现时，将 Implementation Status 改为 `In Progress`。
4. 实现代码完成但测试/验收未完成时，使用 `Implemented`，不得提前标为 `Verified`。
5. 只有验收标准全部满足，且存在可复核证据时，才使用 `Verified`。证据应链接到测试、PR、构建、实机验证记录或相关文档。
6. 若实现偏离已批准决策，应先记录偏差和原因；若架构选择本身改变，应创建新 ADR，并明确取代范围。
7. 被部分取代的 ADR 不得整份标记为 `Superseded`；应在正文明确哪些决策仍然有效、哪些被替代。
8. 不删除已拒绝、废弃或被取代的 ADR。它们是决策历史，后续文档应保留可追溯链接。
9. 不因 PR 合并、代码文件存在、界面已出现或开发者口头确认，就自动认定验收完成。
10. 状态变更应同步更新目录索引和修订记录。

## 评审最低要求

将 Proposed 改为 Accepted 前，ADR 至少应回答：

1. **问题与动机：** 现有架构的具体不足是什么？为什么现在需要决策？
2. **备选方案：** 至少考虑不改变现状及一个可行替代方案；说明选择与放弃的理由。
3. **边界与约束：** 决策适用于什么范围？明确不解决什么问题。
4. **代价与风险：** 长期维护成本、兼容性、安全性、迁移和回滚影响是什么？
5. **事实与假设：** 哪些已经通过代码、实验或文档验证？哪些仍是待验证假设？
6. **验收方式：** 如何判断决策被正确实现？需要哪些测试或实机验证？

若关键事实尚未验证，应保持 Proposed，或在 Accepted 决策中明确列出验证前置条件和禁止越过的安全边界。不要为了让文档看起来完整而编造确定性。

## 文档类型边界

| 文档类型 | 主要回答的问题 | 典型内容 |
|---|---|---|
| ADR | 我们为什么选择这种长期架构？ | 技术选型、模块边界、数据模型、安全边界、兼容性策略 |
| Proposal | 我们是否应该做这件事？ | 用户价值、目标、范围、竞品/替代方案、可行性 |
| Feature Spec | 功能具体应该如何表现？ | 用户流程、交互、状态、异常行为、验收条件 |
| Roadmap / Issue | 接下来由谁做什么？ | 任务拆分、优先级、依赖、进度 |

一份 ADR 可以引用 Feature Spec 或 Proposal，但不应把尚未批准的功能构想写成已确定的架构约束。反过来，功能规格也不应自行改变已有 ADR 的架构决策。

## 统一文件头

新 ADR 使用以下字段与顺序：

```markdown
# ADR NNNN：中文标题

- Decision: Proposed
- Implementation: Not Started
- Date: YYYY-MM-DD
- Scope: 适用范围
- Related: [ADR NNNN](NNNN-example.md)（按需）
- Supersedes: [ADR NNNN](NNNN-example.md)（按需）
- Superseded by: [ADR NNNN](NNNN-example.md)（按需）

## 背景

## 决策

## 备选方案

## 后果

## 验收标准

## 修订记录
```

规则：

1. 编号使用四位数字，按创建顺序递增，不重复、不回收。
2. `Date` 是首次形成该决策的日期；后续修订写入“修订记录”，不修改首次日期。
3. 标题、背景、决策和说明性正文使用中文；技术名词、代码、命令、协议字段和状态值保留原文。
4. `Related`、`Supersedes` 和 `Superseded by` 仅在存在关系时添加，使用相对 Markdown 链接。
5. 已批准 ADR 必须包含可验证的验收标准；纯探索保持 Proposed。
6. 旧 ADR 可以暂时保留旧版 `Status` 字段。迁移时必须依据实际实现证据映射，不得仅凭原状态名称推断已验证。
7. 任何状态不确定时采用更保守的值，并在修订记录说明判断依据。
8. ADR 正文记录架构决策；详细实现方案、私有实现位置和逐项开发任务放在相应文档中。

## 旧状态迁移参考

旧状态映射必须尊重项目维护者确认的实际进展，不得为了满足新字段而回退已经完成的实现或验证。

| 旧写法 | 本次迁移规则 |
|---|---|
| `Proposed` | `Decision: Proposed` + `Implementation: Not Started` |
| `Accepted` | `Decision: Accepted` + 按实际进度填写 Implementation |
| `Accepted (implementation pending)` | `Accepted + Not Started` |
| `In Progress` | `Accepted + In Progress` |
| `Completed` | 本项目既有 Completed 工作已由维护者确认经过实机检测，迁移为 `Accepted + Verified` |
| `Superseded` | `Decision: Superseded` + `Implementation: N/A` |

本次迁移中，原 `Completed` 状态保留为 `Verified`，没有要求重复进行实机检测；这次格式调整不改变技术决策、不撤销已完成工作，也不代表重新启动验收。今后新增或变更状态时，仍需按验收标准记录可复核证据。

## 当前 ADR 索引

以下状态来自各 ADR 当前文件头。Implementation 的 `Verified` 表示既有工作已按项目实际验证状态确认；它不等同于 stable 发布状态。

| ADR | Decision | Implementation | 范围 |
|---|---|---|---|
| [0001 WPF + .NET 10 + 模块化单体](0001-wpf-dotnet10-modular-monolith.md) | Accepted | Verified | 应用基础架构 |
| [0002 元数据驱动的配置系统](0002-metadata-driven-configuration.md) | Accepted | Verified | 配置模型 |
| [0003 public 与 private 仓库分离](0003-public-private-repository-split.md) | Accepted | Verified | 仓库边界 |
| [0004 分阶段启动流水线与 Dry Run](0004-staged-launch-pipeline.md) | Accepted | Verified | 游戏启动流程 |
| [0005 四档下载源策略](0005-download-source-strategy.md) | Accepted | Verified | 下载源策略 |
| [0006 Java 选择、兼容性检查与自动下载](0006-java-management.md) | Accepted | Verified | Java 管理（部分规则由 0014 取代） |
| [0007 加载器安装与组件分发](0007-loaders-and-components.md) | Accepted | Verified | 加载器与组件 |
| [0008 Forge 与 NeoForge 安装器 processors](0008-forge-neoforge-installers.md) | Accepted | Verified | 加载器安装 |
| [0009 CurseForge API 集成与凭据注入](0009-curseforge-integration.md) | Superseded | N/A | CurseForge 客户端集成 |
| [0010 CurseForge 改由私有代理转发](0010-curseforge-private-proxy.md) | Accepted | Verified | 私有代理边界 |
| [0011 全局下载任务与页面解耦](0011-download-task-boundary.md) | Superseded | N/A | 下载任务模型 |
| [0012 整合包归档提取安全边界](0012-modpack-archive-extraction-boundary.md) | Accepted | In Progress | 归档安全 |
| [0013 分支驱动的版本与发布频道](0013-branch-based-versioning-and-release-channels.md) | Accepted | In Progress | 版本与发布 |
| [0014 Java 运行时优先级与兼容性策略](0014-java-runtime-priority-and-compatibility-policy.md) | Accepted | Verified | Java 选择与兼容性 |
| [0015 按进程隔离的游戏日志会话与诊断证据](0015-process-scoped-game-log-sessions-and-diagnostics.md) | Accepted | Verified | 日志与诊断 |
| [0016 独立 Bootstrapper 与安全部署边界](0016-independent-bootstrapper-and-safe-deployment-boundaries.md) | Accepted | Verified | 更新与部署 |
| [0017 图形化首次安装程序](0017-graphical-first-run-installer.md) | Accepted | Verified | 安装器 |
| [0018 Minecraft 工作区与实例存储位置](0018-minecraft-workspace-and-instance-storage.md) | Accepted | Verified | 工作区与实例存储 |
| [0019 内存压力下的进程保活与诊断连续性](0019-memory-pressure-process-survival-and-diagnostic-continuity.md) | Accepted | Verified | 进程生存与诊断 |
| [0020 下载临时产物生命周期与失败清理](0020-download-artifact-lifecycle-and-cleanup.md) | Accepted | Not Started | 下载临时文件 |
| [0021 WPF 交互反馈与轻量动效系统](0021-wpf-interaction-and-motion-system.md) | Proposed | Not Started | WPF 交互 |
| [0022 存储空间分析与安全清理中心](0022-storage-analysis-and-safe-cleanup-center.md) | Proposed | Not Started | 存储分析与清理 |
| [0023 多实例来源目录登记与统一扫描](0023-multi-root-instance-discovery.md) | Proposed | Not Started | 多实例发现 |
| [0024 用户授权的自签名代码证书信任](0024-user-authorized-self-signed-code-signing.md) | Accepted | Not Started | 代码签名信任 |
| [0025 Installer 首次启动完整性确认向导](0025-installer-first-launch-integrity-confirmation.md) | Accepted | Not Started | 安装前完整性确认 |
| [0026 实例工作台的存档、多人游戏与最近访问](0026-instance-workbench-worlds-servers-and-recent-access.md) | Accepted | Not Started | 实例工作台 |
| [0027 原生 C# 世界分析与编辑引擎](0027-native-world-engine.md) | Accepted | Not Started | 世界引擎 |
| [0028 世界种子与生成规则分析功能提议](0028-world-generation-analysis.md) | Proposed | Not Started | 世界生成分析提案 |
| [0029 Installer、Bootstrapper 与 Uninstaller 的生命周期边界](0029-installer-bootstrapper-uninstaller-lifecycle.md) | Accepted | Not Started | 安装与卸载 |
| [0030 世界诊断与区块异常定位联动](0030-world-diagnostics-and-chunk-fault-workflow.md) | Accepted | Not Started | 世界诊断 |
| [0031 多维度世界架构与存储解析](0031-multi-dimension-world-architecture.md) | Accepted | Not Started | 多维度架构 |
| [0032 原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md) | Proposed | Not Started | 功能扩展与数据分发 |
| [0033 本地 Minecraft 全面诊断与知识引擎](0033-local-minecraft-diagnostic-and-knowledge-engine.md) | Proposed | Not Started | 本地诊断与知识库 |
| [0034 ECL 自身完整性与可恢复安装架构](0034-ecl-self-integrity-and-recovery.md) | Proposed | Not Started | 启动器完整性与自修复 |
| [0035 Offline Minecraft Knowledge Library as an Optional Addition](0035-offline-minecraft-knowledge-library.md) | Superseded | N/A | 离线知识库方向（由 0036 取代主要交付方式） |
| [0036 Embedded Browser for Minecraft Knowledge Access](0036-embedded-browser-for-minecraft-knowledge.md) | Proposed | Not Started | 内置浏览器与知识访问 |
