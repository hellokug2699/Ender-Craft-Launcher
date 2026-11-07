# ADR 0062: ECL AI Harness and Controlled Execution Architecture

- Decision: Proposed
- Implementation: Not Started
- Date: 2026-11-08
- Scope: ECL AI Harness Addition 的职责、工具执行编排、Core 安全机制集成、资源授权、文件状态追踪与副作用控制
- Related: [ADR 0032](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0045](0045-native-instance-engine-architecture.md)、[ADR 0047](0047-instance-transactions-and-concurrency-control.md)、[ADR 0048](0048-instance-snapshots-and-recovery.md)、[ADR 0053](0053-addition-capability-permission-and-user-consent.md)、[ADR 0054](0054-addition-registry-and-manifest-governance.md)、[ADR 0055](0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)、[ADR 0061](0061-ecl-ai-conversation-and-capability-architecture.md)

## 背景

ADR 0061 已确定 ECL AI 提供 Chat、Knowledge 和 Diagnostic 三种工作模式，并允许模型通过受控工具获取资料、读取日志与实例资源。随着 AI 能力从单轮问答扩展到多步骤任务，单纯依赖 Prompt 约束和分散的工具调用实现不足以形成一致、可审计的执行边界。

ECL 需要独立的 AI Harness，统一承载模型请求、上下文组装、工具调用分发、执行循环、证据关联、预算控制和安全交互。Harness 按维护者决定作为独立 Feature Addition 交付，而不是隐藏在 ECL AI 内部，也不是 ECL Core 的推理组件。ECL AI 是面向用户的 AI 产品与工作模式；Harness 是其受控执行环境，二者拥有独立 Addition 身份和生命周期。

模型不是可信执行主体。模型输出可能忽略指令、受到提示注入影响、误判资源范围或提出危险操作。ECL 必须遵循自身安全规范：权限由 Core 强制执行，用户同意由 ECL 授权流程表达，资源访问由宿主中介，文件写入遵循 Instance Engine 的事务、并发与恢复约束。Harness 不得建立与这些机制竞争的第二套权限体系。

## 决策

### 1. 将 ECL AI Harness 注册为独立 Feature Addition

- Addition ID：`ecl.ai-harness`
- Name：ECL AI Harness
- Type：`Feature`
- Category：AI & Assistance
- Registry Status：`Proposed`
- `ecl.ai` 使用 Harness 执行模型工具任务时，将 `ecl.ai-harness` 作为必需 Addition。
- Harness 不替代 ECL AI，不提供第二套 Chat、Knowledge 或 Diagnostic 产品入口。
- Harness 不属于 ECL Core，也不进入普通启动或游戏启动链。未安装、禁用、崩溃或版本不兼容时，不得影响 ECL 普通功能。
- Harness 可供未来经审核的 AI Addition 复用，但不得因此默认授予其他 Addition 工具权限或跨 Addition 直接访问能力。

将 Harness 登记为 Addition 是为了隔离 AI 执行依赖、生命周期和安全能力，不意味着把 Core 的权限执行器、Resource Broker、Instance Engine 或用户确认权交给扩展。

### 2. 明确 ECL AI、Harness 与 Core 的职责边界

**ECL AI（`ecl.ai`）负责：** Chat、Knowledge、Diagnostic 工作模式及用户体验；对话、消息、历史、附件和 Shared Knowledge 的产品语义；模型与 Provider 选择、任务意图；向 Harness 提交最小上下文和任务范围。

**ECL AI Harness（`ecl.ai-harness`）负责：** 模型请求与 System Prompt 编排；Context Assembly 和来源关联；工具目录、请求校验、Tool Dispatcher 与受限执行循环；任务预算、超时、取消和故障收敛；工具结果与证据追踪；向 Core 提交权限请求并呈现 Core 返回的授权结果；将工具副作用限制在已授权范围内。

**ECL Core 负责：** Addition 身份和调用者校验；Capability 声明、授权记录、权限判定、撤销和每次调用的强制执行；可信用户授权入口；Resource Broker、受控网络、凭据代理；文件访问、实例身份、事务、并发、快照和恢复等原生能力。

Harness 是安全策略的集成与执行编排层，不是安全授权的最终权威。模型、ECL AI 和 Harness 均不能自行批准权限。

### 3. 所有模型工具调用必须经过 Harness 与 Core 双重边界

模型只能提出结构化工具请求。工具调用必须经过 Harness 校验和调度，再由 Core 对每次受保护能力调用进行最终授权检查。

- 模型不能直接访问文件系统、进程、凭据、网络套接字或任意宿主对象。
- 模型输出、工具参数、网页、搜索结果、日志、模组说明和附件均是不可信数据，不能成为系统指令或授权凭证。
- Harness 只能调用明确登记、具备稳定能力声明且当前版本支持的工具。
- 每次调用绑定当前 Addition 身份、任务、会话、工具能力、资源范围和授权状态。
- Core 独立校验调用者、Capability ID、资源目标、操作类型和当前授权；不能信任 Harness 自报“已批准”。
- 未知工具、未知能力、参数越界、资源范围不匹配、授权过期或权限已撤销时，必须失败关闭。
- 不得通过模型重试、工具别名、路径变体或自动降级绕过拒绝结果。

Harness 可以维护执行策略和预算，但不得复制一套能够独立授予宿主资源访问权的权限数据库。

### 4. 用户授权必须发生在受保护操作之前

Harness 必须直接集成 ADR 0053 的能力声明、用户授权、宿主授予和运行时强制执行模型。用户确认不是模型请求的一部分，而是由 ECL Core 控制的独立授权决策。

授权请求应向用户说明发起主体、准确资源或范围、操作类型、用途、关联任务、授权范围，以及读取内容是否会发送给远程 Provider。用户拒绝、取消、授权超时或授权状态不明确时，不得执行对应操作。

授权必须限定资源、操作和适用范围。新增目标、操作类型或数据范围时重新评估，必要时重新请求授权。仍在有效范围内的授权可以按 ADR 0053 复用，避免同一任务内重复打扰；但只读授权不得升级为写入授权，单实例授权不得扩展为全局文件系统授权。

### 5. 明确区分读取、远程传输与文件修改

Harness 必须区分目录枚举、文件读取、远程传输、文件创建、修改/覆盖、删除/移动/重命名和批量操作，不得使用笼统的“访问文件”权限覆盖全部行为。

读取授权不自动包含远程上传授权。配置远程 Provider 或 API Key 不代表允许上传完整目录、全部日志、聊天历史或其他实例数据。远程请求遵循 ADR 0061 的数据最小化和 Request Inspector 要求。

模型请求修改文件时，Harness 必须将拟执行操作转化为用户可理解的变更说明，并在写入前取得适用的明确授权。应展示目标文件、操作性质、任务目的和可识别的变更范围；能够安全生成差异时应提供差异预览。无法准确说明影响范围的操作不得以宽泛描述请求批准。

### 6. 文件状态追踪必须依赖 Core 原生资源与事务机制

Harness 必须集成 Core 的 Resource Broker、Instance Engine、事务、并发控制和快照/恢复能力；不得绕过这些设施直接写入实例目录。

对于获准的文件修改：
- 修改前记录可用的目标基线状态，例如规范化资源标识、文件存在状态、大小、修改时间和内容哈希；
- 真正写入前重新检查目标，识别授权后发生的外部修改、替换或路径解析变化；
- 检测到状态冲突时暂停，不得覆盖用户或其他进程的新变更；继续执行必须重新取得适用授权；
- 写入通过 Core 支持的事务或受控写入能力执行；
- 完成后记录实际结果和新的文件状态，并关联原始请求、用户授权与执行记录；
- 在适用情况下使用 Instance Engine 的事务恢复材料或快照能力。

状态追踪用于证明执行前后发生了什么，不等于自动回滚、备份保证或完整防篡改审计。若 Core 无法提供所需保护，Harness 必须拒绝相应高风险操作或明确限制能力。

路径检查必须考虑规范化、路径穿越、符号链接/重解析点和检查后目标被替换等风险。资源标识与最终操作目标必须由 Core 在实际访问边界重新验证，不能只依赖 Harness 的字符串路径检查。

### 7. 工具执行受预算、取消和故障策略约束

Harness 应对每个任务维护可审查的执行边界，包括最大工具调用次数、循环深度、耗时、并发任务、响应体积和资源预算。具体阈值由实现阶段依据模型、工具和宿主能力验证确定。

- 模型不能无限循环调用工具。
- 工具超时、取消或失败后，不得自动扩大权限或无限重试。
- 用户取消任务时，Harness 应停止后续模型请求和新工具调用，并通过 Core 对正在执行的可取消操作发出取消请求。
- 对无法安全中断的事务操作，由 Core 决定完成、回滚或进入可恢复状态；Harness 不得伪称已取消或已回滚。
- 工具结果关联任务和来源，避免不同对话、任务或实例的证据串用。
- Harness 崩溃或失去 Core 授权状态时，后续受保护操作必须停止。

### 8. System Prompt 强制应用，但不构成权限

Harness 必须确保每次模型推理请求使用 ADR 0061 定义的适用 ECL System Prompt，并追溯实际使用的 Prompt 版本和组成。不得静默省略、绕过或替换 Prompt；后端无法可靠传递系统指令时应标记不兼容或能力受限，不得无 Prompt 降级。

工具结果、网页、日志、附件和模型生成内容不得提升为 System Prompt 或宿主授权。Prompt 不能批准权限、扩大资源范围或代替 Core 检查。用户应能查看实际生效的 ECL 控制指令，不得暗中追加未披露的 ECL 行为指令。

### 9. 保持 Addition 隔离与宿主资源中介

Harness 与 ECL AI、其他 Addition 之间遵循 ADR 0055 的隔离约束。不得通过进程内共享对象、任意 IPC 或直接访问彼此内部实现绕过宿主边界。Addition 间协作必须经过 Core 受控机制；Harness 不向其他 Addition 暴露 Core 权限令牌、原始凭据或可转授的宿主句柄。

进程隔离不等于完整 OS 沙箱，也不等于 GPU 沙箱。本 ADR 不宣称 Harness 已获得操作系统级安全隔离。具体进程布局由实现阶段验证，但不能削弱权限边界。

### 10. 不在本 ADR 范围内

本 ADR 不规定具体 UI 页面布局、弹窗视觉样式、工具协议字段、SDK、Provider/模型名单、数据库、目录结构、预算数值、具体 IPC/API 签名或进程部署拓扑。具体交互以经确认的 UI 页面规划为准；接口和执行细节由实现设计确定，但不得改变 Core 是最终授权权威、用户授权先于受保护操作、资源读取与远程传输分离、文件变更受 Core 事务与状态检查保护等约束。

## 备选方案

### 方案 A：将 Harness 作为 ECL AI 内部模块

初期结构简单，但执行环境与产品功能共享同一 Addition 生命周期，未来其他 AI 能力难以复用，也容易使工具执行边界与对话产品逻辑耦合。与维护者明确要求 Harness 独立作为 Addition 的方向不符，不采用。

### 方案 B：独立 Harness Addition，但自行管理权限和文件访问

会形成与 ADR 0053、0055 和 Core 资源机制竞争的第二套授权体系，并可能绕过 Instance Engine，安全边界不可接受，不采用。

### 方案 C：独立 Harness Addition + Core 强制权限与资源执行

Harness 独立管理 AI 执行编排、工具循环和证据追踪；Core 保留身份校验、权限裁决、用户授权、资源访问和文件事务的最终控制权。该方案兼顾 Addition 生命周期隔离、可复用性和 ECL 统一安全规范，作为本 ADR 的 Proposed 方向。

## 后果

### 正面影响

- ECL AI 的工具调用、上下文和执行循环具有统一 Harness 边界。
- Harness 可独立安装、更新、禁用和验证，不把 AI 执行依赖塞入 Core。
- 用户授权、权限撤销和资源访问继续使用 ECL 已有安全规范。
- 模型不能自行批准请求，也不能借助 Prompt 或工具结果扩大权限。
- 文件读取、远程传输和文件修改被明确区分。
- 文件变更能够关联修改前状态、用户授权、实际写入结果和修改后状态。
- 实例事务、并发控制、快照和恢复继续由原生 Core 负责。

### 代价与风险

- 新增 Addition 带来独立版本、兼容性、生命周期和依赖治理成本。
- Core 必须提供覆盖完整且不可绕过的能力执行入口。
- 权限请求和变更确认可能增加交互步骤，需要按任务范围复用有效授权以避免授权疲劳。
- 文件状态追踪与并发冲突处理需要和 Instance Engine 深度协调。
- 对任意本机代码或 GPU 执行的安全性不能仅凭 Harness 或进程隔离保证。
- 模型输出仍可能错误；Harness 只能限制副作用和保留证据，不能保证模型结论正确。

## 验收标准

本 ADR 当前为 Proposed，以下为评审与未来实现基线，不表示功能已经实现：

- Registry 中存在独立 Addition `ecl.ai-harness`，类型为 Feature，登记状态与本 ADR 生命周期一致。
- `ecl.ai` 执行模型工具任务时声明对 `ecl.ai-harness` 的必需依赖；缺失时明确显示能力不可用，不得静默绕过 Harness。
- Harness 不提供重复的 Chat、Knowledge 或 Diagnostic 产品入口。
- 模型工具调用只能经过 Harness 调度和 Core 能力执行边界；模型不能直接访问宿主文件系统或系统资源。
- Core 对每次受保护调用重新验证调用者、能力、目标资源、操作类型和当前授权。
- 用户拒绝、取消、授权失效或权限撤销后，相关操作不会发生或继续发起。
- 目录枚举、文件读取、远程传输、创建、修改、覆盖、删除和批量操作具有可区分的能力语义。
- 本地读取不自动授权远程上传；远程传输范围能够被检查和取消。
- 文件修改前执行目标状态复核；检测到外部变更或路径目标变化时停止并进入冲突处理。
- 文件写入经过 Core 受控资源与事务能力，完成后记录可追溯结果状态。
- Harness 不绕过 ADR 0045、0047、0048 的实例所有权、事务、并发和恢复约束。
- System Prompt 在每次推理中强制应用且可追溯；Prompt 和外部内容均不能授予权限。
- 工具循环、耗时、并发和资源预算具备限制；取消、超时和失败不会导致权限扩大。
- Addition 间协作遵循 ADR 0055，不共享内部对象或直接互相访问。
- Harness 不可用或崩溃不影响 ECL 普通启动、实例管理和游戏启动。
- 对路径穿越、符号链接/重解析点、TOCTOU、权限撤销竞态、提示注入、跨任务证据串用、远程数据最小化、文件冲突和失败恢复具备可复核测试。

## 修订记录

- 2026-11-08：依据维护者授权创建 Harness 独立 Addition 架构提案。明确 ECL AI 与 Harness 的产品/执行职责分离，并将 ADR 0053 权限授权、Core 资源中介、Instance Engine 事务和文件状态追踪作为强制安全边界。Decision 保持 Proposed，Implementation 保持 Not Started。
