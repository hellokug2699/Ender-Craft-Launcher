# ADR 0055：ECL 混合工作区与原生工具平台架构

- Decision: Accepted
- Implementation: Not Started
- Date: 2026-11-04
- Scope: ECL 主窗口工作区模型、分割布局、工具与工作区生命周期、Native UI SDK/Host、Addition Runtime 隔离、IPC、Event Bus、资源访问、文档会话及工作区恢复
- Related: [ADR 0032：原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0045：原生 Instance Engine 架构与能力边界](0045-native-instance-engine-architecture.md)、[ADR 0047：实例变更事务与并发控制](0047-instance-transactions-and-concurrency-control.md)、[ADR 0048：实例快照与恢复架构](0048-instance-snapshots-and-recovery.md)、[ADR 0050：ECL UI Architecture and Design System Overhaul](0050-ecl-ui-architecture-and-design-system-overhaul.md)、[ADR 0052：统一 Dialog Engine 与渐进迁移](0052-unified-dialog-engine-and-incremental-migration.md)、[ADR 0053：Additions 能力权限模型与用户授权流程](0053-addition-capability-permission-and-user-consent.md)、[ADR 0054：Addition 注册表、清单与生命周期治理](0054-addition-registry-and-manifest-governance.md)

## 背景

ECL 正在从单一启动器逐步发展为包含日志浏览、NBT/Region 检查、世界分析、资源管理和其他扩展工具的平台。此类工具的界面复杂度差异很大：简单检查器适合在当前页面旁辅助显示，完整编辑器和数据浏览器则需要占据主要工作区域；部分工具还需要长时间运行的后台任务或独立窗口。

如果所有工具都塞进右侧抽屉，复杂工具会缺少空间；如果左右都使用抽屉，主内容区域会被过度压缩；如果每个工具各自创建窗口、管理生命周期和文件访问，又会造成导航、状态恢复、权限和资源占用方式不一致。

Addition 还带来额外边界问题。扩展需要提供足够灵活的界面，但不能任意注入或控制 ECL 主窗口；扩展进程崩溃不能拖垮主程序；多个工具需要协作，但不应直接互相访问内部对象；文件编辑必须与实例事务、并发和恢复机制保持一致。

因此，ECL 需要统一的 Workspace Host 和 Native Tool Platform：ECL 管理工作区、窗口、生命周期、权限与资源边界，原生工具和 Addition 提供具体工具能力及界面描述。该平台应支持单工作区、有限分割和用户主动分离窗口，同时允许 ECL 按工具风险与配置选择 Addition Runtime 的隔离粒度。

本 ADR 的架构决策已获批准，但实施仍为 Not Started。具体 WPF 承载方式、IPC 传输实现、平台沙箱能力和性能阈值必须在实现阶段通过原型与测试验证；本 ADR 不把尚未验证的实现假设描述为既有能力。

## 决策

### 1. 采用由 ECL 管理的混合式 Workspace 模型

ECL 主窗口采用统一 Application Shell 和 Workspace Host。主窗口的职责分区为：

- **顶部区域**：全局操作及当前工作区操作。
- **左侧区域**：全局导航，可折叠。
- **中央区域**：主工作区，承载页面或工具。
- **右侧 Inspector**：当前对象的上下文信息和辅助操作，可隐藏。
- **底部 Output / Task 面板**：日志、任务进度和输出，可收起。

Inspector 和 Output 是辅助区域，不是复杂工具的默认承载区域。需要完整空间的工具必须能够进入中央 Workspace。

Workspace 支持三种布局模式：

- **Single**：单个工作区，默认模式。
- **Split**：用户主动分割工作区，支持水平或垂直分割及可调整比例。
- **Detached**：用户主动将工作区分离到独立窗口，由 ECL 统一管理其身份、生命周期和关闭行为。

默认采用标签式单工作区。Split 和 Detached 是用户主动选择的工作区组织方式，不得强迫普通启动流程理解或配置复杂布局。

### 2. 使用有限嵌套的 Split Tree 管理布局

布局由 Split Tree 表达，只包含布局节点和工作区叶节点，不保存 Addition 的 UI 组件树或业务状态。

- Split Node 表示分割方向、比例及子节点。
- Workspace Leaf 表示一个工作区位置。
- 支持水平和垂直分割。
- 分割比例可调整并可持久化。
- 最大分割嵌套深度为 2 层。
- 同时可见工作区最多为 4 个。
- 删除或关闭工作区后自动清理空节点，并在可能时合并冗余分割节点。
- 小窗口无法满足可用空间约束时，拒绝继续分割并提供改用标签页的路径。
- 分割方向、最小尺寸和 DPI 行为必须遵循 ADR 0050；具体最小尺寸数值由实现阶段通过实际窗口尺寸和可访问性测试确定，不在本 ADR 中锁定。

布局持久化与工作区 Session 持久化分离。调整 Split Tree 不应重建工具业务数据。

### 3. 区分 Tool、Workspace、Runtime、Session 和 Task

这些概念必须保持独立：

- **Tool**：工具定义、能力描述和启动入口。原生工具与 Addition 工具均可被 Workspace Host 管理。
- **Workspace**：工具的一次打开实例，拥有稳定 Workspace ID，并关联操作目标或资源。
- **Runtime**：承载 Addition 代码执行的进程运行实例；一个 Runtime 可承载一个或多个 Workspace，具体由隔离策略决定。
- **Session**：工作区可恢复的视图状态和临时交互状态，例如打开文档引用、筛选条件、树节点展开和选中位置。
- **Task**：由 ECL Task Manager 登记的后台工作。Task 生命周期不依赖工作区是否可见，也不等同于 Workspace 生命周期。

一个 Tool 可以打开多个 Workspace；同一 Addition 可以同时拥有多个 Workspace。不同 Workspace 不得仅因来自同一 Tool 就共享文档状态、资源授权或未隔离的业务状态。

### 4. 由 ECL 统一管理 Workspace 生命周期和关闭保护

Workspace 生命周期至少包括：

`Created → Opening → Ready → Suspended → Closing → Disposed`

并允许进入 `Crashed`、`Unavailable` 等异常状态。

- 创建、关闭、切换、恢复及分离操作由 Workspace Host 管理。
- 切换或暂时隐藏工作区不等于关闭；宿主可按资源策略将其置为 Suspended。
- 关闭工作区必须经过 Close Guard，检查未保存文档、未完成的关键操作及需要用户确认的资源释放行为。
- Addition 可以报告 Dirty State 和关闭建议，但不能自行绕过宿主 Close Guard。
- 用户确认关闭后，宿主协调文档保存/放弃、任务处理、Runtime 资源释放和 Session Snapshot。
- 后台 Task 是否继续、取消或等待，必须由任务策略和用户可见行为决定；隐藏工作区不得隐式取消 Task。
- 工作区 UI 销毁、Addition Runtime 退出、文档关闭和 Task 取消是不同操作，必须分别表达。

### 5. 使用分层快照并在启动时询问是否恢复

工作区恢复采用三个逻辑层次：

- **Layout Snapshot**：Split Tree、分割比例、标签顺序、活动工作区及 Detached 窗口的可恢复布局信息。
- **Workspace Snapshot**：Tool / Addition ID、目标资源引用、Workspace 类型和工具恢复所需的状态引用。
- **Session Metadata**：最后活动时间、Session Schema 版本、创建它的 Tool / Addition 版本及兼容性信息。

ECL 每次启动时询问用户是否恢复上次工作区，并提供恢复上次工作区或进入默认首页的明确选择。

恢复规则：

- 用户选择不恢复时，不启动上次的 Addition Runtime，也不恢复旧后台 Task。
- 用户选择恢复时，逐个检查工具存在性、版本兼容性、资源可用性和 Session Schema。
- 尽可能恢复兼容工作区；某个工作区恢复失败时单独跳过，不阻塞其他工作区或主程序启动。
- 未安装、已禁用、被撤回或不兼容的 Addition 不得被静默下载或启动。
- 恢复工具页面不代表自动恢复之前的后台任务；任务必须由用户或明确的任务恢复策略重新发起。
- Snapshot 只保存恢复状态，不代替文档模型保存未提交的数据，也不保证恢复崩溃前未保存的内存内容。
- Session 迁移必须版本化；未知或不兼容状态应安全丢弃或隔离，不得阻塞启动。

### 6. 采用 ECL Native UI SDK 与宿主渲染模型

Addition 的 UI 使用 ECL Native UI SDK 提供的声明式组件描述，由 ECL Native UI Host 验证并渲染。

职责划分：

- Addition 负责领域逻辑、工具状态和 UI 描述。
- Native UI Host 负责组件树验证、布局约束、原生控件映射、输入事件路由、可访问性和资源生命周期。
- Workspace Host 负责将 UI Host 的视图放入对应 Workspace。
- ECL 控制可用组件、组件属性、事件语义和扩展点。

Addition 不得：

- 任意注入 WPF/XAML、HTML/脚本或任意进程内 UI 代码；
- 直接操作 ECL 主窗口、全局导航、其他 Addition 的 UI 或任意 ViewModel；
- 任意创建或操纵不受 ECL 管理的窗口；
- 绕过 Host 访问 UI 线程、窗口句柄或系统弹窗；
- 以声明式组件负载执行任意代码。

组件库按能力分层，具体清单由 SDK 规范维护，至少覆盖基础输入与展示、布局、数据视图，以及经过评审的高级编辑/媒体组件。大型数据视图应支持虚拟化、分页或增量加载。特殊绘制需求通过受审核的专用组件扩展，不允许以任意 UI 注入作为后门。

Native UI API Version、IPC Protocol Version 和 ECL Product Version 独立演进。Addition 声明其支持的 API 范围；ECL 负责版本兼容性检查和能力协商。组件存在与否、属性支持情况和事件语义均应参与能力协商，不能只依赖版本字符串。

### 7. Addition 使用独立进程，通过 IPC 与 ECL 通信

Addition 运行代码与 ECL 主程序隔离在不同进程中。ECL 主进程不加载 Addition 任意业务程序集到主进程，也不允许 Addition 直接访问主进程内部对象。

逻辑通信路径：

`Addition Process ↔ ECL IPC Gateway / Workspace Bridge ↔ Native UI Host / ECL Services`

IPC 至少需要：

- 握手和进程身份验证；
- 协议版本与能力协商；
- 请求 ID / Correlation ID；
- Runtime ID、Workspace ID 和适用时的 Session ID；
- 消息类型、负载 schema 和大小限制；
- 超时、取消、错误及断连处理；
- 消息速率限制、背压和资源配额。

逻辑上区分 Control Channel 与 Data Channel：

- **Control Channel**：生命周期、UI 描述更新、输入事件、命令、查询、权限申请、Session 状态、任务状态和错误。
- **Data Channel**：大型日志、表格、图像、Region/NBT 数据及其他二进制或大体量数据。必须支持分块、流式处理、背压和取消。

大数据不得通过无限增大的普通控制消息传输。具体传输机制（命名管道、Socket、共享内存或其他平台机制）由实现阶段验证；共享内存只是一种传输方式，不代表获得资源访问权限。

### 8. 采用由 ECL 决定的混合 Runtime Isolation Policy

ECL 根据工具风险、权限上下文、稳定性、用户配置和平台可用隔离能力，决定 Runtime 的承载范围。Addition 可以声明最低隔离要求及运行需求，但不能自行降低隔离等级或要求 ECL 放宽策略。

支持三种运行角色：

1. **Shared Runtime**：同一 Addition 的多个兼容 Workspace 可以共享一个进程。该进程是共同故障边界，进程崩溃会使其承载的全部 Workspace 进入异常状态。
2. **Dedicated Workspace Runtime**：一个 Workspace 使用独立 Runtime，拥有独立进程、IPC 会话和资源配额，适用于需要更强故障隔离或安全边界的工具。
3. **Dedicated Service Runtime**：为跨工作区共享的后台服务提供独立进程，不直接承载工作区 UI；通过 ECL 控制的服务接口和 Event Bus 提供能力。

Runtime Policy Evaluator 必须综合 Addition 身份与信任状态、代码来源、能力请求、资源范围、写入能力、外部数据解析风险、稳定性、用户偏好和操作系统隔离能力作出决定。具体策略矩阵和信任等级属于实现规范，不得由 Addition Manifest 中的自我声明直接决定最终隔离级别。

Shared Runtime 仅在以下条件均满足时允许：

- Addition 身份、版本和运行 API 兼容；
- ECL 策略允许复用，且 Addition 声明允许复用；
- 进程级安全上下文兼容；
- 每个 Workspace 的 Capability Context 和 Resource Scope 仍可独立强制执行；
- 一个 Workspace 不能借用另一个 Workspace 的授权、资源句柄或任务身份；
- 资源配额和消息限流能够控制跨工作区资源影响；
- 共享进程的共同崩溃边界已明确。

不得将多个 Workspace 的权限简单合并为进程级权限并集。Resource Broker 必须按已验证的 Workspace 身份检查每次访问。如果底层环境无法可靠区分工作区授权，必须使用 Dedicated Workspace Runtime。

用户可以要求更严格的隔离；用户配置不能覆盖 ECL 的最低安全要求。若目标隔离能力在当前平台不可实现，ECL 必须拒绝启动或明确降级为不提供该能力的受限功能，不得静默宣称已经满足隔离要求。

Dedicated Service Runtime 是服务角色而非更高权限等级。它必须具有独立身份、能力范围、资源限制、健康状态和故障处理策略；不得成为绕过 Event Bus、Capability Manager 或 Resource Broker 的共享后门。

### 9. 明确 Runtime 故障边界与人工恢复规则

- Addition Runtime 崩溃不得导致 ECL 主进程崩溃。
- Dedicated Workspace Runtime 崩溃只影响对应 Workspace。
- Shared Runtime 崩溃影响该 Runtime 承载的全部 Workspace；ECL 必须如实呈现受影响范围。
- Dedicated Service Runtime 崩溃只影响依赖该服务的功能；其他独立 Runtime 和 ECL 主程序继续工作。
- 崩溃后不自动重启 Addition Runtime，由用户主动选择重启受影响工作区或关闭它们。
- ECL 保留最后一次有效 Session Snapshot，但不承诺恢复崩溃前未保存的内存状态。
- 重启必须创建新的 Runtime ID 和 IPC Session，重新验证身份、协议、兼容性与授权，不得复用失效连接。
- Runtime 退出或被终止时，撤销其临时资源句柄和运行时授权会话；持久化用户授权是否保留由 Capability Manager 的授权规则决定。

独立进程并不自动等于操作系统级沙箱。若 Addition 可执行任意本机代码，单靠 IPC API 约束不能阻止其绕过宿主访问控制。ECL 不得在平台限制尚未实现并验证前宣称具备完整沙箱。此项约束与 ADR 0053 保持一致。

### 10. Addition 之间只通过 ECL Event Bus 协作

Addition 不得直接连接其他 Addition 进程，不得读取其他 Addition 的内存、文件句柄、Session 或内部状态。工具间通信必须经由 ECL Workspace Event Bus / Service API。

消息语义区分为：

- **Event**：通知已发生的事实，不要求返回值。
- **Command**：请求执行操作，必须返回结果或明确错误。
- **Query**：请求读取信息，必须进行调用者身份和数据范围授权检查。

命名空间建议：

- `ecl.workspace.*`
- `ecl.minecraft.*`
- `ecl.resource.*`
- `ecl.diagnostics.*`
- `addition.<addition-id>.*`

Addition 只能发布其获准的自身命名空间消息，不能冒充 ECL 系统事件。订阅必须显式登记并经过授权。Event 默认不持久化；需要持久化的业务事实必须单独定义保留策略、版本和数据最小化要求。

Event Bus 不承担大文件或大型二进制内容传输。事件应传递受控资源引用或结果标识，大型数据通过 Data Channel / Resource Broker 获取。订阅者崩溃不得阻塞发布者；高频消息支持合并、节流、限流和背压。

### 11. 文件与文档访问统一经过 Resource Broker 和 Document Service

Addition 不因运行在独立进程中就获得用户文件系统的无限访问权限。

**Resource Broker** 是受控资源访问入口，负责验证 Addition / Runtime / Workspace 身份、已授予 Capability、资源范围、操作类型、资源有效性和并发约束。Addition 使用受控资源引用和操作接口，不获得可用于任意文件访问的通用凭证。权限检查必须与 ADR 0053 的声明、授予和执行模型衔接；ADR 0053 在 Proposed 期间，其具体授权交互细节仍待该 ADR 正式决策。

**Document Service** 负责可编辑文档会话的生命周期和数据完整性，包括 Open、Dirty Tracking、Revision Tracking、Lock Management、Save Transaction、Conflict Detection 和 Recovery。Workspace 持有 Document Session 引用，不直接拥有文件写入权。

保存前必须检查文档 Revision 或等价并发令牌，避免外部修改被静默覆盖。检测到冲突时不得无条件覆盖；应提供重新加载、另存为或在格式支持时进行合并的路径。NBT、Region 等复杂二进制格式不得假定能够自动合并。

Resource Broker / Document Service 不取代 Instance Engine。涉及实例文件、世界存档和实例级变更时，必须继续遵循 ADR 0045、ADR 0047 和 ADR 0048 的所有权、事务、并发、快照与恢复规则。

### 12. 将 Workspace State、Snapshot、Event 和 Task 分开管理

ECL 提供以下共享服务：

- **Workspace State Store**：当前运行期间可共享的短期上下文，不作为长期持久化来源。
- **Session Snapshot Store**：退出或异常前可恢复的工作区状态，不替代领域数据持久化。
- **Workspace Event Bus**：跨工具的受控事件、命令和查询路由。
- **Task Manager**：后台任务登记、进度、取消、资源使用及生命周期。
- **Compatibility Manager**：ECL Product Version、Native UI API Version、IPC Protocol Version 和 Session Schema 的兼容性判断与迁移协调。

状态所有权必须明确：

| 状态 | 权威所有者 |
|---|---|
| Workspace ID、标签、布局、Detached 窗口 | ECL Workspace Host |
| Addition 工具内部视图状态 | 对应 Addition |
| UI 组件树的验证与渲染状态 | Native UI Host |
| 文档修改状态与保存事务 | Document Service |
| 权限授权与撤销 | ECL Capability Manager |
| 后台 Task 登记和状态 | ECL Task Manager |
| 工具领域数据 | 对应 Addition 或领域引擎 |
| 跨工作区短期共享上下文 | ECL Workspace State Store |

任何状态跨越进程边界时都必须通过明确的协议和授权接口，不得依赖共享内存中的隐式对象引用。

### 13. 与既有 ADR 的职责边界

- ADR 0032 继续定义 Addition 的产品交付模式、Core 与 Addition 能力边界、签名校验和按需加载。本 ADR 补充 Addition 的工作区 UI 和进程运行模型，不取代 ADR 0032。
- ADR 0050 继续作为 UI 全局架构和设计系统的最高优先级约束。本 ADR 定义 Workspace Host 的领域架构，不另建一套全局导航或设计系统。
- ADR 0052 继续负责通用 Dialog Engine、模态关系和弹窗生命周期。Workspace Host 通过统一 Dialog Engine 请求弹窗，不复制弹窗基础设施。
- ADR 0053 负责能力声明、用户授权和运行时权限执行。本 ADR 定义 Runtime/Workspace 的身份边界及其与权限执行的关系，不替代能力目录和授权交互决策。
- ADR 0054 负责 Addition Registry、Manifest 和登记治理。本 ADR 的 Runtime Policy 输入可以读取经验证的 Manifest 声明，但 Manifest 不得直接决定最终隔离等级。
- ADR 0045、0047、0048 继续约束实例资源、事务、并发、快照和恢复。本 ADR 的 Resource Broker / Document Service 必须遵循这些领域规则。

ADR 0053 和 ADR 0054 当前为 Proposed。本 ADR 与其建立职责关系，不代表将其提案内容提前认定为已实施事实；相关接口细节应在这些 ADR 完成评审后对齐。

### 14. 分阶段实施，先建立宿主边界再开放复杂扩展

建议实施顺序：

1. **Workspace Core**：Workspace Host、Layout Tree、标签、生命周期、Close Guard 和基础 Snapshot。
2. **Resource & Document Foundation**：Resource Broker、Document Session、Revision / Dirty Tracking，并复用 Instance Engine 事务能力。
3. **Native UI Platform**：Native UI Host、SDK、组件 schema、输入事件和 API 兼容协商。
4. **Addition Runtime Foundation**：Process Manager、IPC Gateway、Runtime Identity、Policy Evaluator 和 Dedicated Workspace Runtime。
5. **Shared Runtime & Service Runtime**：实现受控进程复用、服务角色、独立故障边界和配额。
6. **Workspace Services**：Event Bus、State Store、Task Manager、Compatibility Manager。
7. **Detached Windows & Recovery Hardening**：独立窗口、跨窗口布局恢复、崩溃诊断和迁移。

不得在 IPC 身份验证、权限强制执行、资源访问边界和崩溃隔离未验证前开放任意第三方 Addition 代码执行。

## 备选方案

### 方案 A：所有工具固定显示在右侧或底部抽屉

实现简单，但复杂工具空间不足，且会让主内容区与工具 UI 产生强耦合。不能满足编辑器、树形浏览器和大型表格等工作负载。

### 方案 B：每个工具自行创建窗口和管理生命周期

工具可以自由选择 UI，但会重复实现窗口管理、关闭保护、恢复、权限、任务和资源访问；工具之间也难以统一交互。不采用。

### 方案 C：每个 Workspace 固定独立进程

故障边界清晰，但会增加进程数量、内存和启动开销；对低风险、轻量工具造成不必要成本。保留为强隔离模式，不作为唯一模式。

### 方案 D：同一 Addition 的所有 Workspace 固定共享一个进程

资源开销较低，但所有工作区共享崩溃边界；若权限上下文无法严格区分，还可能造成授权串用。保留为满足严格条件时的 Shared Runtime 模式，不作为默认强制模式。

### 方案 E：由 ECL 管理的混合 Workspace 与混合 Runtime 隔离

由统一 Workspace Host 管理 UI 和生命周期，按风险与配置决定共享、独立工作区或服务 Runtime；工具间通信经由 Event Bus，资源访问经由 Broker。该方案在资源成本、扩展能力、故障隔离和安全边界之间取得可调整的平衡，作为正式决策。

## 后果

### 正面影响

- 简单工具和复杂编辑器拥有适合自己的工作区空间。
- 用户可以在单工作区、分割工作区和独立窗口之间选择。
- 工作区、Addition Runtime、文档和后台任务生命周期分离，避免相互误触发。
- ECL 可以根据风险与运行环境调整隔离粒度，而不是为所有工具承担相同的进程成本。
- Shared Runtime 的共同故障边界被明确，不再错误承诺进程内工作区完全独立。
- Native UI SDK 允许 Addition 提供原生风格工具界面，同时保留宿主对窗口、组件和交互的控制。
- Event Bus、Resource Broker 和 Document Service 为多工具协作及安全文件编辑提供统一边界。
- 未安装或崩溃的 Addition 不应破坏 ECL 普通启动和其他独立 Runtime。

### 代价与风险

- Workspace Host、Layout Engine、Session Manager 和 Native UI Host 增加了基础设施复杂度。
- 声明式 UI SDK 需要维护组件 schema、版本协商、输入事件和可访问性语义。
- IPC 会引入序列化、延迟、背压、超时、断连和进程管理成本。
- Shared Runtime 的工作区隔离需要严格的身份绑定和权限检查；任何旁路都可能造成越权。
- Dedicated Runtime 增加进程、内存和启动开销；Runtime Policy 需要持续维护。
- Document Service 需要处理外部修改、锁、保存失败和格式差异，不能承诺所有文件都可自动合并。
- 独立进程本身不是完整 OS 沙箱；对任意本机代码的安全承诺受平台能力限制。
- Session Snapshot、API 版本和 Addition 版本之间需要长期兼容与迁移测试。

## 验收标准

- ECL 默认以单工作区启动；不配置或不安装 Addition 不影响普通启动和游戏启动。
- 用户可以创建标签工作区、水平/垂直分割工作区和 Detached 工作区。
- Split Tree 不保存 Addition 组件树或业务对象；最大分割深度为 2，最多同时显示 4 个工作区。
- 窗口空间不足时拒绝创建不可用分割，并提供标签页替代路径。
- Workspace、Runtime、Session、Document 和 Task 生命周期可以独立操作和测试。
- 关闭未保存文档时，Close Guard 阻止静默丢弃；Addition 无法绕过宿主关闭保护。
- 用户启动时可以选择恢复或进入默认首页；不恢复时不会启动上次 Addition Runtime 或恢复旧 Task。
- 恢复失败的单个工作区不会阻塞其他工作区和主程序启动。
- Addition UI 只能通过 Native UI SDK 描述受支持组件；任意 UI/XAML/脚本注入不能进入主进程。
- ECL 与 Addition 使用独立进程及经过身份验证的 IPC；不兼容 API / Protocol 不会加载。
- Runtime Policy 由 ECL 决定，Addition 不能降低隔离要求。
- Shared Runtime 不会将多个 Workspace 的 Capability 集合简单合并；资源访问按 Workspace 身份独立检查。
- Shared Runtime 崩溃时，所有承载工作区被明确标记为受影响；其他独立 Runtime 和 ECL 主进程继续工作。
- Dedicated Workspace Runtime 崩溃只影响对应工作区；Dedicated Service Runtime 故障只影响其依赖方。
- Addition 崩溃后不自动重启；用户主动重启时创建新的 Runtime / IPC Session 并重新验证授权。
- Addition 之间不存在绕过 ECL Event Bus 的直接通信路径。
- 文件访问经 Resource Broker；可编辑文档经 Document Service，保存前检查 Revision，冲突时不静默覆盖。
- 涉及实例文件的操作遵循 ADR 0045、0047 和 0048 的约束。
- UI、Runtime、IPC、Session Schema 版本分别管理，并通过能力协商检查兼容性。
- 在 OS 级隔离能力未实现并验证前，产品文档不宣称 Addition 具备完整沙箱。
- 存在自动化测试或可复核实验覆盖 IPC 断连、恶意/畸形消息、超限消息、Runtime 崩溃、权限串用、Session 迁移和文档冲突。

## 修订记录

- 2026-11-04：根据 ECL 混合工作区、Native UI SDK、Addition 独立进程、混合 Runtime 隔离、Event Bus 和受控资源访问讨论形成决策。Decision 为 Accepted，Implementation 为 Not Started；具体技术实现和平台隔离能力仍须在实施阶段验证。
