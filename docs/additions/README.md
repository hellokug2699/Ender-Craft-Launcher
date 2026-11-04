# ECL Additions Registry

> **提案草案 — 尚未成为正式注册目录。** 本目录结构依据 ADR 0032 与拟议中的 ADR 0054 建立；在 ADR 0054 获得批准前，以下内容仅用于评审和迁移规划，不代表其中的项目已经正式注册或可供安装。

本页面是 ECL 官方 Additions 的统一目录入口。所有正式登记的 Feature Addition 与 Data Addition 都应在此列出，并链接到各自的说明页与机器可读 Manifest。

## 治理规则

- 扩展架构遵循 [ADR 0032：原生功能入口与按需 Additions 扩展体系](../adr/0032-native-feature-interfaces-and-optional-additions.md)。
- 注册、身份、Manifest、生命周期和迁移规则以 [ADR 0054：Addition 注册表、清单与生命周期治理](../adr/0054-addition-registry-and-manifest-governance.md) 为准；该 ADR 当前为 Proposed。
- 权限声明和用户授权遵循 [ADR 0053：Additions 能力权限模型与用户授权流程](../adr/0053-addition-capability-permission-and-user-consent.md)。ADR 0053 当前为 Proposed，其内容尚不是已批准的架构约束。
- ADR 记录架构决策；本目录记录扩展注册与发布状态。不能根据 ADR 状态推断 Addition 是否可安装。
- 只有具备有效 Manifest、兼容性声明和可验证发布资产的 Addition 才能标记为 `Available`。

## 生命周期状态

| 状态 | 含义 |
|---|---|
| `Proposed` | 候选，尚未完成登记审核 |
| `Planned` | 已通过登记审核，尚未进入实质开发 |
| `In Development` | 正在开发或准备发布 |
| `Available` | 存在可验证且兼容的正式发布资产 |
| `Deprecated` | 不再推荐新用户安装，提供迁移说明 |
| `Withdrawn` | 已停止分发，保留历史记录 |

上述是 Addition Registry 状态，不是 ADR Decision / Implementation 状态，也不是本地 Installed / Enabled / Loaded 状态。

## 官方 Additions

> 当前尚未完成正式登记。下表仅列出需要评审和迁移的候选方向；候选项目不得被视为已经发布或可安装。

| 候选项目 | 类型 | 候选来源 | 当前说明 |
|---|---|---|---|
| 原版工具集（Vanilla Toolkit） | Feature / Data（待拆分确认） | [ADR 0040](../adr/0040-vanilla-toolkit-addition.md) | ADR 已提出独立官方 Addition，并给出示例 ID；尚无正式 Registry 条目与可发布资产 |
| 性能监控 | Feature | [ADR 0041](../adr/0041-minecraft-performance-monitoring-and-profiling-architecture.md) | ADR 提出基础监控 Addition；正式 ID、包边界和发布资产待登记审核 |
| 性能分析器 | Feature | [ADR 0041](../adr/0041-minecraft-performance-monitoring-and-profiling-architecture.md) | ADR 提出高级分析 Addition；正式 ID、backend 分发和许可待确认 |
| 网络诊断 | Feature | [ADR 0044](../adr/0044-local-first-network-diagnostics-and-data-sovereignty.md) | ADR 明确作为可选 Addition；正式 ID 与打包边界待登记审核 |
| GitHub 项目中心 | Feature | [ADR 0049](../adr/0049-native-github-integration-and-repository-bounded-navigation.md) | ADR 明确作为 Feature Addition；正式 ID、Manifest 与发布资产待登记审核 |
| 本地诊断知识与规则数据 | Data（待确认） | [ADR 0033](../adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md) | 需区分 Core 诊断基础设施、Feature 能力与独立知识数据，不能将整份 ADR 直接等同于 Addition |

### 历史方向

- [ADR 0035：离线 Minecraft 知识库](../adr/0035-offline-minecraft-knowledge-library.md) 已由 ADR 0036 取代其以大规模离线知识包作为主要知识入口的方向。保留原有历史状态，不作为当前候选扩展登记，也不恢复其已被取代的方案。

## 登记结构

每个正式 Addition 应拥有独立目录：

```text
docs/additions/
├── README.md
└── <addition-id>/
    ├── README.md
    └── manifest.json
```

- 本 README 是唯一总目录。
- 单项 README 说明用途、边界、兼容性、依赖、权限摘要、限制和相关决策。
- Manifest 提供 Core 和发布工具读取的机器可读元数据。
- 大型包体通过现有公有仓库的发布资产分发，不提交到文档目录。
- 正式 ID 分配前，候选条目不创建正式 Addition 子目录。

## 登记与发布流程

1. 提出候选 Addition，说明目标、类型、功能边界和独立交付理由。
2. 审查 Core / Addition 边界、重复能力、依赖、权限、许可与隐私。
3. 审核通过后分配稳定 Addition ID，创建单项说明与 Manifest。
4. 完成实现、兼容性、安全和清单校验。
5. 发布包资产、签名和校验信息，并同步更新本目录。
6. 维护版本、弃用、撤回和替代关系。

**登记不等于发布。** 在正式发布资产可验证之前，条目不得显示为 Available。

## 相关文档

- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 总目录](../adr/README.md)
