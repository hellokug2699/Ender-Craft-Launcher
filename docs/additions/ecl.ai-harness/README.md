# ECL AI Harness

- Addition ID: `ecl.ai-harness`
- Type: `Feature`
- Category: AI & Assistance
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL AI Harness 是独立的 AI 受控执行环境 Addition。它不提供第二套聊天或诊断产品入口，而是为 ECL AI 提供统一的模型请求编排、上下文组装、工具调用、执行循环、预算控制和证据追踪能力。

ECL AI 负责 Chat、Knowledge、Diagnostic 产品模式及对话体验；Harness 负责把模型提出的任务请求转换为经过验证、授权和受控执行的工具操作。Harness 不拥有最终权限裁决权，也不能绕过 ECL Core 的安全规范。

## 核心职责

- 组装本次模型请求所需的最小上下文，并保留来源关联。
- 确保每次推理使用适用且可追溯的 ECL System Prompt。
- 管理工具目录、工具请求校验、Tool Dispatcher 和受限执行循环。
- 限制工具调用次数、循环、耗时、并发和资源预算。
- 关联工具结果、来源、适用版本、任务和文件状态等证据。
- 将需要额外权限的请求交给 ECL Core 授权机制，并呈现真实授权结果。
- 在授权范围内执行工具操作，将结果和失败原因反馈给 ECL AI。

## 安全与权限边界

- 模型只能提出请求，不能直接访问本机文件系统、凭据、进程、网络套接字或任意宿主对象。
- 所有受保护资源访问均经过 Harness 调度和 Core 最终强制检查。
- Capability 声明、用户授权、宿主授予、权限撤销和运行时检查遵循 [ADR 0053](../../adr/0053-addition-capability-permission-and-user-consent.md)。
- 用户授权必须发生在受保护访问或副作用之前；授权范围必须明确限定资源、操作和任务。
- 目录枚举、文件读取、远程传输、创建、修改、覆盖、删除和批量操作不得混为同一种权限。
- 本地读取不自动授权向远程 Provider 上传；远程传输继续遵循 ECL AI 的数据最小化与请求透明度要求。
- 模型输出、网页、搜索结果、日志、模组说明和附件均是不可信输入，不得成为权限指令。
- 拒绝、取消、授权撤销、未知能力或越界请求必须失败关闭。

## 文件状态追踪

对于用户明确批准的文件修改，Harness 必须集成 Core 的 Resource Broker、Instance Engine、事务、并发控制和快照/恢复能力：

- 修改前记录可用的目标基线状态；
- 写入前重新验证目标身份与状态，检测外部修改和路径目标变化；
- 发生冲突时暂停，不得覆盖新变更；
- 通过 Core 受控事务或写入能力执行；
- 修改后记录实际结果，并关联请求、授权和执行记录。

状态追踪不等于自动回滚或备份保证。路径规范化、路径穿越、符号链接/重解析点和 TOCTOU 风险必须在 Core 实际访问边界处理。Harness 不得绕过 Instance Engine。

## 依赖

- Required Additions：无（Harness 本身不要求 ECL AI 才能安装）。
- Consumers：`ecl.ai` 将其作为执行模型工具任务的必需 Addition。
- Optional Additions：无。
- Core API Compatibility：待定义。
- Capability / Permission：遵循 ADR 0053；具体 Capability ID 和清单待实现评审，不在当前 Manifest 中虚构。
- 与 ECL AI 的协作必须经过 Core 受控机制，不允许 Addition 间直接访问内部对象或任意 IPC。

## 兼容性与发布

- AI Provider / Model Compatibility：按实际模型、Provider 和工具调用能力验证，不承诺所有模型均支持相同能力。
- Core API Compatibility：待定义。
- Release Assets：暂无。
- License / Data Sources：代码许可及可能使用的模型/工具数据来源须在发布前审核。

## 相关文档

- [ADR 0062：ECL AI Harness and Controlled Execution Architecture](../../adr/0062-ecl-ai-harness-and-controlled-execution-architecture.md)
- [ADR 0061：ECL AI Conversation and Capability Architecture](../../adr/0061-ecl-ai-conversation-and-capability-architecture.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0055：ECL 混合工作区与原生工具平台架构](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0047：实例变更事务与并发控制](../../adr/0047-instance-transactions-and-concurrency-control.md)
- [ADR 0048：实例快照与恢复架构](../../adr/0048-instance-snapshots-and-recovery.md)
- [ECL Additions Registry](../README.md)

## 修订记录

- 2026-11-08：依据 ADR 0062 创建候选登记条目。状态为 Proposed，尚无正式发布资产，不可安装。
