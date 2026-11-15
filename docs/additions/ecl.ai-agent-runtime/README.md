# ECL AI Agent Runtime

- Addition ID: `ecl.ai-agent-runtime`
- Type: `Feature`
- Category: AI & Assistance
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL AI Agent Runtime 是独立的 AI 受控执行环境 Addition。它不提供第二套聊天或诊断产品入口，而是为 ECL AI 提供统一的模型请求编排、上下文组装、工具调用、执行循环、预算控制和证据追踪能力。

ECL AI 负责 Chat、Knowledge、Diagnostic 产品模式及对话体验；Agent Runtime 负责把模型提出的任务请求转换为经过验证、授权和受控执行的工具操作。Agent Runtime 不拥有最终权限裁决权，也不能绕过 ECL Core 的安全规范。

## 核心职责

- 管理 Target、Plan、Task Graph 与任务生命周期。
- 根据依赖、资源冲突、预算和授权状态调度任务。
- 管理 Change Set，并提供统一 Changes/Changed Files 审查语义。
- 记录任务检查点，协调暂停、恢复、取消和失败收敛。

- 组装本次模型请求所需的最小上下文，并保留来源关联。
- 确保每次推理使用适用且可追溯的 ECL System Prompt。
- 管理工具目录、工具请求校验、Tool Dispatcher 和受限执行循环。
- 限制工具调用次数、循环、耗时、并发和资源预算。
- 关联工具结果、来源、适用版本、任务和文件状态等证据。
- 将需要额外权限的请求交给 ECL Core 授权机制，并呈现真实授权结果。
- 在授权范围内执行工具操作，将结果和失败原因反馈给 ECL AI。

## Target、Plan 与 Task Scheduler

- Target 表达任务目标和资源范围，不等于权限授予。
- Plan 记录步骤、依赖、预期产物、风险和验收条件，并保留修订历史。
- Task Scheduler 支持依赖驱动的顺序/安全并行执行、暂停、恢复、取消和失败阻断。
- 并行任务必须检测共享资源冲突；不得同时无协调地修改同一资源。
- 任务重试必须考虑幂等性；执行结果不确定时先检查实际状态。
- 任务恢复时重新验证目标身份、资源状态和授权，不复用过期读取证据。

## Change Set 与 Changes Review

- 将同一计划中各任务产生的变更汇总为可审查的 Change Set。
- 显示拟议/已应用变更、文件差异或准确摘要、来源任务、授权和验证状态。
- 保留每项变更的独立来源与状态，不把不同任务的变更合并到无法追溯的结果。
- Changes 面板的查看、勾选或审查不构成写入授权；所有写入仍由 Core 强制检查。
- 用户修改导致基线变化时检测冲突，不得静默覆盖。
- 恢复/撤销必须使用 Core 事务、快照或恢复能力，并检查当前资源状态。

## Checkpoint 与恢复

长任务应在适当边界保存可恢复状态。恢复时重新检查 Target、资源状态、权限、授权和必要读取证据。无法确认先前副作用是否发生时，标记为不确定并请求介入，不得假定未执行。

## 安全与权限边界

- 模型只能提出请求，不能直接访问本机文件系统、凭据、进程、网络套接字或任意宿主对象。
- 所有受保护资源访问均经过 Agent Runtime 调度和 Core 最终强制检查。
- Capability 声明、用户授权、宿主授予、权限撤销和运行时检查遵循 [ADR 0053](../../adr/0053-addition-capability-permission-and-user-consent.md)。
- 用户授权必须发生在受保护访问或副作用之前；授权范围必须明确限定资源、操作和任务。
- 目录枚举、文件读取、远程传输、创建、修改、覆盖、删除和批量操作不得混为同一种权限。
- 本地读取不自动授权向远程 Provider 上传；远程传输继续遵循 ECL AI 的数据最小化与请求透明度要求。
- 模型输出、网页、搜索结果、日志、模组说明和附件均是不可信输入，不得成为权限指令。
- 拒绝、取消、授权撤销、未知能力或越界请求必须失败关闭。

## 文件状态追踪

对于用户明确批准的文件修改，Agent Runtime 必须集成 Core 的 Resource Broker、Instance Engine、事务、并发控制和快照/恢复能力：

- 修改前记录可用的目标基线状态；
- 写入前重新验证目标身份与状态，检测外部修改和路径目标变化；
- 发生冲突时暂停，不得覆盖新变更；
- 通过 Core 受控事务或写入能力执行；
- 修改后记录实际结果，并关联请求、授权和执行记录。

状态追踪不等于自动回滚或备份保证。路径规范化、路径穿越、符号链接/重解析点和 TOCTOU 风险必须在 Core 实际访问边界处理。Agent Runtime 不得绕过 Instance Engine。


## Read-Before-Write 与读取覆盖要求

Agent Runtime 必须在 Core 写入边界强制执行以下三个条件：

- **Read-Before-Write：** 每次写入必须有绑定当前任务、会话和目标资源的有效读取证据。未读取目标文件时不得写入。
- **Read-State Consistency：** 读取后目标内容、身份或状态发生变化，旧读取证据和基于旧内容生成的修改结果均失效。必须重新读取、重新分析并重新生成修改；Core 还必须在实际提交写入时再次验证目标状态。
- **Read-Coverage Sufficiency：** 读取范围必须覆盖本次修改所需的上下文。仅凭模型自我声明“已读够”不能通过检查；必要的关联定义、调用点、配置关系或结构约束未被检查时，必须补充读取或停止写入。

读取覆盖不足不意味着必须无条件读取整个文件。Agent Runtime 应按需扩大读取范围：
1. 定向读取目标位置、邻近上下文及已识别的直接关联内容；
2. 必要时进行结构性读取，检查文件结构、相关定义和引用；
3. 只有整体重构、全局修改、影响范围不明确或局部/结构性证据不足以建立可靠覆盖时，才要求完整读取。

Token 成本不能成为跳过必要上下文的理由；同样，完整读取也不应成为所有局部修改的机械前置条件。结构摘要和搜索结果可以辅助定位，但不能冒充目标内容已被读取。无法建立充分覆盖时必须失败关闭。

读取证据应绑定目标资源身份、读取时状态、任务/会话和拟修改范围，并在目标变化、授权撤销或任务结束等条件下失效。读取覆盖通过仅表示上下文前置条件满足，不代表用户授权或其他安全检查自动通过。



## Execution Feedback 与自我修正

Agent Runtime 必须让模型观察到工具执行和验证的真实结果，并支持受控的自我修正闭环：

- **Execution Feedback：** 工具结果必须如实区分成功、失败、部分完成、取消、超时和状态不确定；工具调用成功不等于任务成功。
- **Error Classification：** 尽可能区分语法/解析、类型/编译、测试、运行时、环境/依赖、权限、资源冲突和基础设施错误。保留原始诊断与来源；无法可靠分类时标记未知，不伪造确定性。
- **Diagnostic Context：** 在可获得时向模型提供错误位置、相关输出和定位线索。诊断行号或片段不能替代有效文件读取证据；修复仍遵循本 README 的读取与状态一致性规则。
- **Self-Correction Loop：** 模型可以分析反馈、提出修正并再次调用工具。修正不能绕过 Core 授权、扩大既有资源范围或重放已失效的文件修改结果。
- **Bounded Recovery：** 自动修正必须受工具调用次数、修正轮数、耗时和资源预算限制。重复失败、预算耗尽、状态不确定或需要扩大授权时停止自动循环并报告。
- **Verification：** 根据任务选择可用的语法检查、编译、测试或运行验证；验证结果必须关联实际工具、目标和环境。
- **Honest Completion：** 区分工具操作成功、文件写入、静态/语法检查、编译、测试和实际运行验证。未执行或不可用的检查不得声称通过；部分通过和失败必须如实呈现。

工具输出、编译器诊断、测试结果和外部执行内容均是不可信输入，不能修改系统指令或授予权限。验证失败不自动意味着可以执行新的副作用；所有修正仍经过既有授权和 Core 强制检查。


## 依赖

- Required Additions：无（Agent Runtime 本身不要求 ECL AI 才能安装）。
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

- [ADR 0062：ECL AI Agent Runtime and Controlled Execution Architecture](../../adr/0062-ecl-ai-agent-runtime-and-orchestration-architecture.md)
- [ADR 0061：ECL AI Conversation and Capability Architecture](../../adr/0061-ecl-ai-conversation-and-capability-architecture.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0055：ECL 混合工作区与原生工具平台架构](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0047：实例变更事务与并发控制](../../adr/0047-instance-transactions-and-concurrency-control.md)
- [ADR 0048：实例快照与恢复架构](../../adr/0048-instance-snapshots-and-recovery.md)
- [ECL Additions Registry](../README.md)

## 修订记录

- 2026-11-08：依据 ADR 0062 创建候选登记条目。状态为 Proposed，尚无正式发布资产，不可安装。
- 2026-11-08：同步 ADR 0062，补充读取先行、读取状态一致性和读取覆盖充分性要求，并明确按需扩展读取范围。
- 2026-11-08：同步 ADR 0062，补充执行反馈、错误分类、自我修正循环、验证与诚实完成状态要求。
- 2026-11-08：根据 ADR 0062 的扩展提案更名为 ECL AI Agent Runtime，增加 Target、Plan、Task Scheduler、Change Set/Changes Review 与 Checkpoint/恢复职责。
