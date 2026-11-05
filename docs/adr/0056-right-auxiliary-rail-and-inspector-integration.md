# ADR 0056: Right Auxiliary Rail and Inspector Integration

- Decision: Proposed
- Implementation: Not Started
- Date: 2026-11-05
- Scope: ECL 主窗口右侧辅助区域、工具入口可发现性、Inspector 上下文面板及其与中央 Workspace 的边界
- Related: [ADR 0050: ECL UI Architecture and Design System Overhaul](0050-ecl-ui-architecture-and-design-system-overhaul.md)、[ADR 0055：ECL 混合工作区与原生工具平台架构](0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)

## 背景

ADR 0050 §3 将右侧区域定义为可折叠工具栏：默认折叠为窄图标栏，展开后展示工具图标与名称。ADR 0055 §1 将右侧区域定义为 Inspector，用于呈现当前对象的上下文信息和辅助操作。两项决策均为 Accepted，且 ADR 0050 §1.2 明确其全局 UI 规则优先级。

如果直接选择其中一项，会导致另一项已批准决策被隐式废弃；如果同时创建两个独立的右侧面板，又会压缩中央 Workspace，并产生两个相互竞争的辅助区域。工具入口和 Inspector 的职责其实不同：前者负责发现与启动工具，后者负责展示当前对象上下文。二者可以共享同一右侧辅助区域，但不能混成同一种内容。

本 ADR 提出一个复合式右侧辅助区域，作为对两项既有决策的协调方案。当前仍为 Proposed；在维护者明确批准前，不改变 ADR 0050/0055 的状态与效力，也不得据此完成 Shell 视觉重构。

## 决策提案

### 1. 右侧采用统一 Right Auxiliary Rail

主窗口右侧建立统一的 Right Auxiliary Rail，由窄轨道和一个可切换内容面板组成：

- **Rail（窄轨道）**：始终提供可折叠的辅助入口；折叠状态显示图标，展开状态可显示图标和名称。
- **Inspector 模式**：展示当前选中对象的上下文摘要和少量辅助动作。
- **Tools 模式**：展示可用工具入口、工具用途和安装/兼容/权限状态。
- 两种模式共享同一右侧区域，不同时占据两个独立列；切换模式不得丢失 Inspector 的选择上下文或用户已打开的工具筛选状态。

该结构把 ADR 0050 的工具入口保留为 Rail，把 ADR 0055 的 Inspector 保留为 Rail 的一种内容模式，不将任一职责转移到中央 Workspace。

### 2. 工具入口只负责发现和启动

Tools 模式只展示工具清单，不承载工具的主要工作界面：

- 点击工具入口后，在中央 Workspace 打开或激活对应 Workspace。
- 已有 Workspace 再次从 Tools 模式启动时，按稳定 ToolId/WorkspaceTarget 激活现有工作区，不重复创建等价标签。
- 未安装、不兼容、依赖缺失或权限不足的工具可以显示状态与说明，但不得伪装为可执行。
- 工具入口可以提供安装/获取说明，但不能自行绕过 Addition Registry、Capability Manager 或统一安装流程。
- 简单、只读的当前对象辅助信息可以由 Inspector 展示；完整编辑器、日志浏览器、NBT Explorer、世界工作台等复杂工具必须进入中央 Workspace。

### 3. Inspector 的职责与状态

Inspector 模式分为两层：

- **Workspace Context**：当前工作区的标题、工具身份、生命周期、目标资源、未保存状态等基础信息，由 Shell 提供。
- **Selection Context**：当前工作区选中对象的只读摘要与少量辅助动作，由活动页面通过受控契约提供。

规则：

- Inspector 不承载页面主体或复杂编辑流程。
- 隐藏 Inspector 不得使当前页面失去任何关键操作。
- 切换工作区时，Inspector 上下文必须随活动工作区切换；不能展示其他工作区的过期选择。
- 当前页面没有可展示的选择上下文时，显示工作区上下文或明确空状态。
- 页面只能提供自身工作区的 Inspector 内容，不得直接写入其他工作区的 Inspector 状态。

### 4. 默认状态与交互

- 首次启动默认进入 Inspector 模式；若没有可展示内容，显示轻量空状态。
- 用户可折叠/展开 Rail，并在 Inspector 与 Tools 模式间切换。
- Rail 的展开状态和当前模式可作为 Shell 偏好持久化；不得与 Workspace Snapshot 或业务 Session 状态混为一谈。
- 键盘可访问：Rail 入口具有名称、焦点顺序和明确选中态；不能只依赖悬停 Tooltip。
- 窄窗口下优先折叠 Rail，不能挤压中央 Workspace 至不可用宽度。
- 当前窗口宽度不足时，Tools 模式只展示精简入口；不能把复杂工具页面塞入 Rail 作为降级方案。

### 5. 与全局工具中心的边界

Right Auxiliary Rail 的 Tools 模式是快速入口，不替代完整工具中心：

- 工具中心用于完整发现、分类、搜索和管理工具。
- Rail 用于高频工具的快速访问和当前上下文相关工具入口。
- 两者必须复用同一 Tool Registry / Availability 查询结果，不得维护两份工具列表或两套安装状态。
- 工具中心页面是否作为全局导航目的地，由页面体系迁移规划单独确定。

### 6. 实施约束

- 不新增第二个 Inspector 或第二个工具注册表。
- 不让工具入口直接创建任意窗口或修改主导航。
- 不改变 ADR 0055 定义的 Workspace、Tool、Runtime、Session、Task 身份与生命周期。
- 不改变 Addition 权限和文件访问边界。
- Rail 的状态持久化不应自动恢复工具进程、后台任务或未保存文档。
- 实现前必须通过最小原型验证中央 Workspace 的可用宽度、键盘导航、DPI 和窗口缩放行为。

## 备选方案

### 方案 A：只保留 Inspector，工具全部移入全局工具中心

优点：右侧职责单一，主窗口结构简单。

缺点：没有满足 ADR 0050 对右侧工具栏的既有决策，且高频工具发现路径变长。除非明确修订 ADR 0050，否则不能直接采用。

### 方案 B：只保留右侧工具栏，移除 Inspector

优点：满足 ADR 0050 的既有右侧工具入口。

缺点：失去 ADR 0055 定义的当前对象上下文与辅助操作；需要把 Inspector 信息迁移到其他位置，容易造成重复或上下文丢失。

### 方案 C：同时显示两个独立右侧面板

优点：两个既有职责都能同时可见。

缺点：增加固定横向占用，挤压中央 Workspace；小窗口和高 DPI 下风险较高；两者的折叠、焦点和状态同步复杂。

### 方案 D：Right Auxiliary Rail 复合区域（推荐）

优点：保留工具入口与 Inspector 两种职责，共享同一空间，不将复杂工具内容放进右侧；可以逐步实现。

代价：需要定义模式切换、选择上下文和 Rail 偏好契约，并验证响应式布局。

## 后果与风险

- 工具发现和当前对象上下文具有明确分工。
- 右侧只占用一列，不会形成两个并排的辅助面板。
- Tools 模式不能取代工具中心；若两者不复用同一注册表，会产生状态漂移。
- Inspector 选择上下文需要跨工作区正确隔离，不能继续使用全局单例选择状态。
- 具体最小宽度、图标数量、展开宽度和响应断点必须通过原型与实机验证，不在本 ADR 中编造固定数值。

## 验收标准

- [ ] Rail 折叠时可通过键盘访问所有入口，并有可访问名称。
- [ ] Inspector 与 Tools 共用同一右侧区域，不同时形成两个独立面板。
- [ ] Tools 入口只启动/激活中央 Workspace，不在 Rail 内呈现复杂工具主界面。
- [ ] Inspector 显示当前活动工作区的上下文，不泄漏其他工作区的选择状态。
- [ ] 隐藏 Rail 后，中央页面的关键操作仍然可用。
- [ ] Tools 模式与完整工具中心复用同一工具注册/可用性数据。
- [ ] 窄窗口、100%/150% DPI 和键盘导航下布局可用。
- [ ] 不改变 ADR 0055 的 Workspace/Runtime/Session/Task 生命周期与权限边界。

## 修订记录

- 2026-11-05：首次提出 Right Auxiliary Rail，用于协调 ADR 0050 右侧工具栏与 ADR 0055 Inspector 两项既有决策；当前为 Proposed，等待维护者评审。
