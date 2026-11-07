# ECL Additions Registry

> 本目录是 ECL 官方 Addition 的唯一登记入口。**登记不等于发布**：当前登记条目均无正式 Release Asset，不代表已经可以下载或安装。

ECL 采用“原生功能入口、可选功能能力”的产品模型：页面、入口和未安装提示由 ECL 原生提供；Feature Addition 提供可选执行能力；Data Addition 提供可独立更新的数据内容。扩展架构遵循 [ADR 0032](../adr/0032-native-feature-interfaces-and-optional-additions.md)，注册治理遵循 [ADR 0054](../adr/0054-addition-registry-and-manifest-governance.md)，能力授权遵循 [ADR 0053](../adr/0053-addition-capability-permission-and-user-consent.md)。

## 治理规则

- 本目录登记具有独立身份和生命周期的可选用户能力；不是 Core 架构组件、原生基础设施或普通 Workspace/Tool 页面清单。
- **架构/引擎不自动等于 Addition。** Native World Engine 是 ECL Core 的原生架构与基础引擎；由它支撑的 NBT Explorer、World Region Selector、World Generation Analysis 等独立用户功能仍分别登记为 Feature Addition。
- 第三方产品名称可以作为功能参照，不代表 ECL 依赖、集成或分发该产品。World Region Selector 是 ECL 自研功能，MCA Selector 仅作能力参照；World Generation Analysis 是 ECL 自研功能，Chunkbase 仅作产品体验参照。
- 原生页面和基础服务不因 Addition 缺失而消失；未安装时展示功能说明和安装提示。
- Registry 状态与 ADR Decision/Implementation、本地 Installed/Enabled/Loaded 状态相互独立。
- 只有具备有效 Manifest、兼容性声明、签名/完整性信息和可验证正式发布资产的条目才可标记为 `Available`。
- Manifest 中尚未确定的 Core API、权限、兼容范围、许可证或数据来源明确标记为待定义/待审核，不以猜测值填充。

## 生命周期状态

| 状态 | 含义 |
|---|---|
| `Proposed` | 候选，尚未完成登记审核 |
| `Planned` | 已通过登记审核，尚未进入实质开发 |
| `In Development` | 正在开发或准备发布 |
| `Available` | 存在经过校验、兼容的正式发布资产 |
| `Deprecated` | 不再推荐新用户安装，提供迁移说明 |
| `Withdrawn` | 已停止分发，保留历史记录 |

## 官方 Additions

### Feature Additions

| Addition | ID | 状态 | 说明 | 依据 |
|---|---|---|---|---|
| NBT Explorer | [`ecl.nbt-explorer`](ecl.nbt-explorer/README.md) | Planned | NBT 结构浏览、检索与受控编辑 | [ADR 0027](../adr/0027-native-world-engine.md) |
| World Region Selector | [`ecl.world-region-selector`](ecl.world-region-selector/README.md) | Planned | ECL 自研的地图化 Region/Chunk 选择与管理；不依赖 MCA Selector | [ADR 0027](../adr/0027-native-world-engine.md) |
| World Generation Analysis | [`ecl.world-generation-analysis`](ecl.world-generation-analysis/README.md) | Planned | ECL 自研 Seed、Biome 与 Structure 预测；不依赖 Chunkbase | [ADR 0028](../adr/0028-world-generation-analysis.md) |
| Schematic Preview | [`ecl.schematic-preview`](ecl.schematic-preview/README.md) | Planned | 投影/建筑方案预览与空间查看 | Feature Spec 待完善 |
| Minecraft Diagnostic Engine | [`ecl.diagnostic-engine`](ecl.diagnostic-engine/README.md) | In Development | 本地日志、崩溃与环境诊断，包含 Crash Analysis | [ADR 0033](../adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md) |
| Vanilla Toolkit | [`ecl.vanilla-toolkit`](ecl.vanilla-toolkit/README.md) | In Development | 原版规则查询、计算、模拟与辅助工具 | [ADR 0040](../adr/0040-vanilla-toolkit-addition.md) |
| Performance Monitor | [`ecl.performance-monitor`](ecl.performance-monitor/README.md) | In Development | 低开销性能健康监控 | [ADR 0041](../adr/0041-minecraft-performance-monitoring-and-profiling-architecture.md) |
| Performance Profiler | [`ecl.performance-profiler`](ecl.performance-profiler/README.md) | In Development | 按需高级性能分析 | [ADR 0041](../adr/0041-minecraft-performance-monitoring-and-profiling-architecture.md) |
| Network Diagnostics | [`ecl.network-diagnostics`](ecl.network-diagnostics/README.md) | In Development | 本地优先网络诊断 | [ADR 0044](../adr/0044-local-first-network-diagnostics-and-data-sovereignty.md) |
| GitHub Project Center | [`ecl.github-project-center`](ecl.github-project-center/README.md) | In Development | ECL 原生 GitHub 项目协作中心 | [ADR 0049](../adr/0049-native-github-integration-and-repository-bounded-navigation.md) |
| ECL Multiplayer | [`ecl.multiplayer`](ecl.multiplayer/README.md) | Planned | ECL 自研私人联机体验与会话管理；不提供公共大厅 | [ADR 0057](../adr/0057-ecl-multiplayer-session-architecture.md)（Accepted） |
| Resource Pack Studio | [`ecl.resource-pack-studio`](ecl.resource-pack-studio/README.md) | Proposed | 资源包原始浏览、材质/模型/动画与状态变体预览；不模拟 Minecraft 世界 | [ADR 0058](../adr/0058-resource-pack-studio-architecture.md)（Proposed） |
| Shader Studio | [`ecl.shader-studio`](ecl.shader-studio/README.md) | Proposed | 光影包浏览、结构与兼容性分析、固定场景视觉预览；官方标准与高级场景统一交付 | [ADR 0059](../adr/0059-shader-studio-and-standard-scene-environment-architecture.md)（Proposed） |
| ECL AI | [`ecl.ai`](ecl.ai/README.md) | Planned | 统一 Chat、Minecraft Knowledge 与 Diagnostic 工作模式；支持本地/远程推理和受控联网搜索 | [ADR 0061](../adr/0061-ecl-ai-conversation-and-capability-architecture.md)（Accepted） |
| ECL AI Harness | [`ecl.ai-harness`](ecl.ai-harness/README.md) | Proposed | AI 受控执行环境：上下文组装、工具编排、权限请求集成、执行预算与证据/文件状态追踪 | [ADR 0062](../adr/0062-ecl-ai-harness-and-controlled-execution-architecture.md)（Proposed） |

### Data Additions

| Addition | ID | 状态 | 说明 | 依据 |
|---|---|---|---|---|
| Diagnostic Knowledge Base | [`ecl.diagnostic-knowledge-base`](ecl.diagnostic-knowledge-base/README.md) | Planned | 可独立更新的故障模式、诊断规则与兼容知识 | [ADR 0033](../adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md) |
| Vanilla Toolkit Data | [`ecl.vanilla-toolkit-data`](ecl.vanilla-toolkit-data/README.md) | Planned | Vanilla Toolkit 消费的版本化原版结构化数据 | [ADR 0040](../adr/0040-vanilla-toolkit-addition.md) |
| Publisher Trust Database | [`ecl.publisher-trust-database`](ecl.publisher-trust-database/README.md) | Planned | 发布者、项目与分发来源验证数据，为安全中心提供本地参考 | [ADR 0043](../adr/0043-minecraft-threat-protection-and-security-analysis.md) |

## 待形成正式登记的候选

以下方向仍处于产品讨论，尚未形成正式 Addition 登记：


- **World Generation Data**：是否需要从 World Generation Analysis 中拆出独立 Data Addition，待算法与数据分发边界明确后决定。

## 原生能力与非 Addition 边界

以下项目属于 ECL 原生 Core、架构或原生 UI，不应作为可选 Addition 登记：

- **Native World Engine**：原生世界解析、存储、Schema、注册表解析和渲染基础架构；不是 Addition。
- **Native Instance Engine**：实例身份、资源清单、文件访问、事务与恢复基础设施；不是 Addition。
- **World Workbench**：原生 Workspace/工具入口；不是一个大型 Addition。
- **Instance File Explorer**：基础实例文件浏览由 ECL 原生提供；NBT/Region 等高级处理能力由对应 Addition 提供。
- **Security Center / Threat Protection**：原生安全中心与安全分析工作流；Publisher Trust Database 是可选数据扩展，不等于安全中心本身。
- **Wardrobe**：ECL 原生 Tool，不是 Addition。

## 历史方向

- [ADR 0035：离线 Minecraft 知识库](../adr/0035-offline-minecraft-knowledge-library.md) 已由 ADR 0036 取代其以大规模离线知识包作为主要知识入口的方向。保留历史状态，不恢复旧方案，也不将其登记为当前 Addition。

## 登记结构

每个正式 Addition 拥有独立目录：`docs/additions/<addition-id>/{README.md,manifest.json}`。本 README 是唯一总目录；大型包体和数据包通过现有公有仓库 Release Assets 分发，不提交到文档目录。

当前 Manifest 使用 `manifestSchemaVersion: 1.0.0`。配套 JSON Schema 和自动一致性校验器仍需补齐；在此之前，Manifest 的待定义字段不得被解释为运行时可直接安装的完整契约。

## 登记与发布流程

1. 提出候选 Addition，说明目标、类型、功能边界和独立交付理由。
2. 审查 Core/Addition 边界、重复能力、依赖、权限、许可与隐私。
3. 审核通过后分配稳定 Addition ID，创建单项说明与 Manifest，并加入本目录。
4. 完成实现、兼容性、安全和 Manifest Schema 校验。
5. 发布包资产、签名和校验信息，并同步更新本目录。
6. 维护版本、弃用、撤回和替代关系。

**登记不等于发布。** 当前条目均未声明正式发布资产，不得显示为 Available。

## 相关文档

- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 总目录](../adr/README.md)
