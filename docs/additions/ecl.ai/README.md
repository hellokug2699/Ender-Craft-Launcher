# ECL AI

- Addition ID: `ecl.ai`
- Type: `Feature`
- Category: AI & Assistance
- Registry Status: `Planned`
- Current Version: 尚未发布

## 概述

ECL AI 是 ECL 统一的 AI 功能扩展，提供通用聊天、Minecraft 知识问答和日志诊断能力。三个工作模式共享对话、上下文、模型、搜索、凭据和安全基础设施，不需要分别安装。

ECL AI 不绑定单一模型厂商，也不要求用户只能讨论 Minecraft。用户可以选择本地模型、自己配置的远程 Provider 或自建模型服务；远程 Provider 产生的费用由用户承担。

## 工作模式

### Chat

通用聊天入口，不限制用户讨论主题，不强制附加 Minecraft 实例上下文。

### Knowledge

帮助玩家了解 Minecraft 原版机制、模组、Loader、配置和相关技术。可以按需通过 ECL 受控网络能力搜索官方文档、模组资料、项目仓库、Issue 和社区 Wiki。本地模型同样可以联网搜索；本地推理不等于离线运行。

### Diagnostic

帮助玩家分析日志、崩溃报告和运行环境，解释证据、推断可能原因并提供建议。基础诊断不依赖其他 Addition；安装现有诊断引擎或诊断知识库可以提供额外结构化证据和知识增强。

## 推理后端

目标支持：

- Local Model：用户设备本地推理。
- Remote Provider：用户自行配置 Provider、模型和 API Key。
- Self-hosted / LAN Provider：用户自行部署或局域网模型服务。
- Hybrid Routing：在用户配置与授权后按任务选择后端。

ECL 不提供由 ECL 付费承担的公共 AI API。具体 Provider、模型能力、用量和费用以实际服务商为准；ECL 不得把估算值显示成准确账单。

## 上下文与对话

- Chat、Knowledge、Diagnostic 共用对话管理能力。
- 多对话默认完全隔离，不自动读取其他对话的历史、附件或摘要。
- 跨对话资料通过用户管理的 Shared Knowledge 明确共享。
- 聊天记录与单次模型上下文分离；上下文按任务和授权范围按需构建。
- 长对话可使用带来源追踪的摘要，但摘要不能替代原始日志或其他证据。
- 对话默认本地保存，并支持临时对话、删除和导出。

## 隐私与安全边界

- API Key 由 Core 安全凭据能力管理，不放入普通配置、Prompt、聊天记录或日志。
- 配置 API Key 不代表同意上传全部聊天历史、日志、附件或实例信息。
- 远程请求遵循数据最小化，并提供上下文/请求检查能力。
- System Prompt 的来源、生成逻辑和版本公开；用户能够查看当前实际使用的 ECL 控制指令。
- Prompt 不得暗中索取无关数据、上传隐私或绕过授权；Prompt 本身不构成权限控制。
- 文件、网络、凭据和工具调用必须经过 Core 的真实权限机制。
- 搜索结果、网页、Issue、日志和附件均作为不可信输入处理。

## 可选增强

以下现有 Addition 可增强 Diagnostic，但不是 ECL AI 的必需依赖：

- [Minecraft Diagnostic Engine](../ecl.diagnostic-engine/README.md)：提供结构化诊断事件、规则结果和证据关联。
- [Diagnostic Knowledge Base](../ecl.diagnostic-knowledge-base/README.md)：提供可独立更新的故障模式、兼容知识和经过审核的解决方案。

缺少这些扩展时，ECL AI Diagnostic 仍提供基础分析，并说明当前证据和知识覆盖范围。

## 依赖与权限

- Required Additions：`ecl.ai-harness`（执行模型工具任务时必需；Harness 缺失时不得静默绕过安全执行层）。
- Optional Additions：`ecl.diagnostic-engine`、`ecl.diagnostic-knowledge-base`。
- Core API Compatibility：待定义。
- Capability / Permission：待实现评审与登记；本条目不授予任何权限。
- 具体文件、网络、凭据和工具能力必须遵循 ADR 0053 及宿主运行时授权机制。

## 兼容性与发布

- Minecraft / Loader Compatibility：Chat 不要求 Minecraft 环境；Knowledge 和 Diagnostic 的具体环境上下文按实际能力声明。
- Local Model Compatibility：按模型格式、硬件和实际验证结果声明，不承诺所有设备均可运行。
- Remote Provider Compatibility：按 Provider Adapter 与能力验证结果声明。
- Release Assets：暂无。
- License / Data Sources：代码许可、模型许可及外部资料来源须在发布前逐项审核。

## 相关文档

- [ADR 0061：ECL AI Conversation and Capability Architecture](../../adr/0061-ecl-ai-conversation-and-capability-architecture.md)
- [ECL AI Harness](../ecl.ai-harness/README.md)
- [ADR 0062：ECL AI Harness and Controlled Execution Architecture](../../adr/0062-ecl-ai-harness-and-controlled-execution-architecture.md)
- [ADR 0033：本地 Minecraft 全面诊断与知识引擎](../../adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ECL Additions Registry](../README.md)

## 修订记录

- 2026-11-08：依据 ADR 0061 建立 ECL AI 登记条目；状态为 Planned，尚无正式发布资产，不可安装。
- 2026-11-08：依据 ADR 0062 增加 `ecl.ai-harness` 必需执行依赖；ECL AI 的产品入口与三种工作模式保持不变。
