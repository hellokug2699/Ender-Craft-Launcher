# Application Shell、Workspace 与 Dialog Engine

本文记录第一批 UI 基础设施的实现契约、迁移状态与后续扩展点。架构约束仍以
[ADR 0050](adr/0050-ecl-ui-architecture-and-design-system-overhaul.md)、
[ADR 0052](adr/0052-unified-dialog-engine-and-incremental-migration.md) 和
[ADR 0055](adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md) 为准；
本文只说明当前代码做到了什么、没有做什么。

## 1. Shell 分区

主窗口（`src/ECL.App/Shell/MainWindow.xaml`）固定分为五个职责区域：

| 区域 | 内容 | 实现 |
| --- | --- | --- |
| 顶部栏 | 品牌、导航开关、当前工作区标题、辅助面板开关 | `MainWindow.xaml` |
| 左侧导航 | 分组的高层目的地，可折叠 | `ShellViewModel` + `NavigationView` |
| 中央 Workspace Host | 工作区标签与页面内容，占据主要空间 | `ShellViewModel` + `PageHost` |
| 右侧 Inspector | 当前工作区上下文，可隐藏 | `Shell/Inspector` |
| 底部 Output / Task | 任务进度与应用输出，可收起 | `Shell/Output` |

顶部栏与辅助面板开关都支持键盘：`Ctrl+B` 折叠导航、`Ctrl+Shift+I` 切换 Inspector、
`Ctrl+Shift+O` 切换 Output、`Ctrl+Tab` / `Ctrl+Shift+Tab` 切换工作区、`Ctrl+1…9` 激活第 N 个导航目的地。

修饰性视觉全部来自 `Themes/Theme.xaml` 的语义令牌（含 `ScrimBrush`、Shell 图标 PathGeometry、
`PanelToggleButton`、`DialogCard`）；页面没有新增局部颜色、字号或圆角。

布局尺寸同样来自令牌：`PageInset`（页面外围内边距，只由 `PageHost` 使用）、`SidebarWidth`、
`InspectorWidth`、`PageMaxContentWidth`、`SectionSpacing`。Shell 不再直接写死 248 / 296 / `28,22,28,18`。

## 2. 导航与工作区

- 全局导航只选择高层目的地，不再承担标签或布局职责。
- 每个目的地对应一个工具标识（`ShellNavigationItem.ToolId`），点击后在中央 Workspace Host 打开或复用该工作区。
- 页面 ViewModel 仍由 `App.xaml` 的 `DataTemplate` 映射为视图；工作区只切换内容，不直接构造视图。
- 切换标签只改变活动工作区，不销毁页面 ViewModel；关闭标签才会移除工作区。
- 导航高亮跟随活动工作区；实例工作区没有独立导航项，因此导航不显示选中态。

### 2.1 工作区身份与去重（B0）

工作区身份固定为 `(ToolId, TargetResource)`，并且只有一个实现：

| 类型 | 职责 |
| --- | --- |
| `WorkspaceTarget` | 强类型目标：`Kind`（`Destination` / `Legacy`）、`SchemaVersion`、规范化资源 |
| `WorkspaceTargetSerializer` | 唯一的版本化序列化器：语法校验、规范化、旧值兼容；**不做**文件授权与路径边界校验 |
| `WorkspaceTargetCompatibility` | 旧裸实例 ID 与 `instance:<id>` 的一次性规范化与等价判断 |
| `WorkspaceToolId` | ToolId 语法：全小写 ASCII，`tool.<name>` 只允许一段 |
| `WorkspaceDedupKey` | 去重键 `(ToolId, 规范化 TargetResource)`，Ordinal 比较 |

- B0 只登记目的地与“旧裸资源”两种目标；`instance:v1:`、`file:`、`world:` 目标类型在各自消费者批次（B2 / File / World）引入，
  当前构建遇到这些前缀会显式拒绝，而不是退化成任意字符串。
- 旧 `instance:<id>` 与裸 `<id>` 规范化后是同一身份，同一会话不会同时存在两种等价标签。
- 路径越界、符号链接与重解析点校验属于受控资源访问服务（ADR 0018/0022/0042/0045），不进入通用 serializer。

三个统一入口（页面不得直接操作 `Workspaces` 集合）：

| 入口 | 语义 |
| --- | --- |
| `ActivateDestination(key)` | 解析导航 Key / ToolId / 旧别名后选中导航项，并打开或复用目的地工作区 |
| `OpenWorkspace(toolId, targetResource, title)` | 任意工作区；`(ToolId, TargetResource)` 相同则复用 |
| `OpenInstanceWorkspace(instanceId)` | `instance` + 规范化实例身份的便捷入口 |

失败处理：ToolId 非法、目标无法解析或没有页面工厂时返回 `false` 并记录日志，不创建空壳工作区。

### 2.2 旧 Key 与深链接兼容

- `java` 的旧 Key 通过 `NavigationAliases` 重定向到 `settings`；**别名只在同名导航 Key 不存在时生效**，
  因此在 B3 真正移除 `java` 目的地之前，行为与旧版完全一致。
- `ECL_START_PAGE` 接受导航 Key、ToolId 与旧别名，大小写不敏感；无法解析时回退第一个目的地并记录警告，不出现白屏。
- `Ctrl+1…9` 仍按当前导航顺序绑定；导航项变化时序号随之变化。

工作区身份、生命周期与关闭判定位于 UI 无关的 `ECL.Core.Workspace`：

| 类型 | 职责 |
| --- | --- |
| `WorkspaceId` / `WorkspaceDescriptor` | 稳定标识、工具标识与目标资源引用 |
| `WorkspaceLifecycleState` / `WorkspaceEntry` | `Created → Opening → Ready → Suspended → Closing → Disposed` 及 `Crashed` / `Unavailable` |
| `WorkspaceLayout` / `WorkspaceLayoutNode` | 不可变 Split Tree：分割深度 ≤ 2、同时可见 ≤ 4、比例限定在 0.2–0.8、关闭后折叠空节点 |
| `WorkspaceHost` | 登记、激活、挂起、异常状态、关闭与可见布局；`FindByTarget` 复用同一去重语义 |
| `IWorkspaceCloseGuard` / `DefaultWorkspaceCloseGuard` | 未保存状态需要用户确认；已关闭状态拒绝重复关闭 |

对应测试位于 `tests/ECL.Tests/Workspace/`，覆盖目标序列化与规范化、旧值兼容、畸形输入、去重键、深度与数量上限、
非法节点、关闭折叠、脏状态关闭确认、生命周期迁移与布局引用校验。

### 尚未实现（有明确扩展点）

- **Split 交互**：布局模型与约束已经实现并测试，但 Shell 目前只以单工作区（标签）方式呈现；
  分割按钮、拖拽比例和失败提示尚未提供，因此 UI 不展示任何“分割”控件。
- **Detached Window**：没有独立窗口生命周期。
- **Session Snapshot / 启动恢复询问**：尚未持久化布局或工作区状态；启动仍进入默认目的地。
- **Addition Runtime / Native UI Host / IPC / Event Bus / Resource Broker / Document Service**：不属于本批范围。

## 3. 共享页面控件（B0）

B0 只提供 B1 立即需要的三个共享件，其余控件（`DataTable`、`StatusBanner`、`InlineProgress`、
`SearchBox`、`FilterPill`、`SortSelector`、`SeverityBadge`、`MonoText`、扩展图标集）在第一个真实消费者批次实现，
避免建立只有单一消费者的抽象。

| 控件 | 位置 | 职责 |
| --- | --- | --- |
| `PageHeader` | `src/ECL.App/Controls/PageHeader.xaml` | 目的地页面的标题 + 副标题 + 标题行右侧操作槽；不可见项自动折叠 |
| `PageStateView` | `src/ECL.App/Controls/PageStateView.xaml` | 加载 / 空 / 错误 / 不可用四态；状态用文字标签 + 颜色双重表达 |
| `ToolbarButton` | `Themes/Theme.xaml` | 页面标题行与工具条的次级操作，紧凑但仍保留 32px 高度与焦点环 |

- `PageHeader` 与 `PageStateView` 的默认文案走本地化键 `page.state.*`；页面覆盖说明时必须写清“为什么”和“怎样解除”。
- 四态视图只呈现状态，不自行发起命令；可执行入口通过 `ActionContent` 由页面提供。
- 两个控件都有 STA 冒烟测试（`tests/ECL.App.Tests/Controls/`），确保令牌与样式在页面接入前就能加载。
- 页面根元素不得再叠加外围外边距；外围边距由 `PageHost` 统一提供。

## 4. Inspector

Inspector 只投影活动工作区的宿主元数据（标题、工具、生命周期、目标资源、打开时间、未保存标记），
并提供“关闭工作区”这一真实动作（仍走 Close Guard）。没有活动工作区时显示空状态。
它不复制页面内容，也不持有任何业务数据或独立导航能力。

B0 增加了**选择层**的最小契约（ADR 0056 §3）：

- `InspectorSelectionContext` / `InspectorSelectionField`：当前选中对象的只读摘要（标题、可选副标题、只读字段）。
- 选择层由 Shell 下发与清空；切换活动工作区或关闭工作区时自动清空，避免跨工作区残留选中对象。
- 选择层不承担编辑，也不会成为第二个编辑面板；关键操作必须留在中央页面。
- 当前消费者：实例列表页（B1）在选中实例时提供实例名、版本、加载器、创建日期与最后游玩摘要。

## 5. Output / Task 面板

- 任务视图直接复用既有 `OperationCenterViewModel`（`IOperationStore` 快照）；没有第二套任务登记。
- 输出视图读取 `AppLogBuffer`：固定容量 2000 行、UI 最多显示 500 行，按日志级别着色，支持清空。
  它只是应用级日志观察窗，不替代文件日志或游戏进程日志会话。
- 面板开合、切换标签、收起都不启动、暂停、取消或释放任何任务。

## 6. Dialog Engine

- 入口：`IDialogEngine.ShowAsync` / `Show`；宿主：`DialogLayerViewModel` + `Dialogs/Views/DialogHostView`。
- 模态弹窗打开时，`ShellRoot.IsEnabled` 由 `DialogLayerViewModel.IsShellInteractive` 绑定禁用，
  整窗遮罩（`ScrimBrush`）覆盖导航、主内容与 Inspector；弹窗本身位于遮罩之上的独立层。
- 弹窗栈只显示最上层弹窗；父弹窗在下层保留，子弹窗关闭后恢复父弹窗与焦点。
- 尺寸受主窗口客户区约束（`DialogHostView` 动态计算），内容过多时在弹窗内部滚动。
- 关闭语义区分 `Primary` / `Secondary` / `Cancel` / `Closed` / `Failed`；忙碌状态拒绝普通关闭。
- 焦点：打开时记录原焦点，内容与宿主协作把焦点移入弹窗；关闭后恢复原焦点（元素仍可用时）。
  Escape 仅在请求允许关闭时生效；单操作弹窗才设置默认按钮，破坏性操作初始焦点在取消按钮。
- 动效遵循 `MotionSettings`（减少动态效果时直接进入最终状态），关闭不等待动画。

### 迁移状态

已迁移到 Dialog Engine 的弹窗入口（`Services/DialogService.cs`）：

- `Confirm`（确认、破坏性操作确认）
- `ChooseInstanceDeletion`（三选一删除模式）
- `ShowInfo` / `ShowError`

暂时保留的兼容路径：

- 主窗口弹窗宿主尚未附加（首次启动向导、启动失败）时回退到系统 `MessageBox`，避免丢失用户操作。
- 文件选择继续使用 Windows 原生对话框。
- `WorkspaceSetupWindow`、`NbtExplorerWindow`、`WorldWorkbenchWindow`、`VersionImportDialog`、
  `AuthlibAccountDialog` 仍是旧窗口实现，尚未迁移。

## 7. 线程与状态通知契约

### 7.1 Dialog Engine

- `DialogLayerViewModel`、`DialogEntryViewModel` 与所有绑定属性只归创建它们的 WPF Dispatcher 所有；
  在错误线程上构造或修改会抛出 `DialogThreadAffinityException`，而不是静默跨线程写绑定状态。
- `ShowAsync` 可从任意线程调用：后台调用会被封送到 UI 线程创建弹窗，并返回同一个结果任务。
  `Show` 使用嵌套消息循环实现同步模态，只能在 UI 线程调用。
- CancellationToken 回调可能在线程池执行；引擎把关闭请求封送到 UI 线程，重复关闭/取消竞争由 UI 线程串行化，
  每个弹窗任务只完成一次。
- 关闭父弹窗时，仍打开的嵌套子弹窗先按 `Closed` 完成，再完成父弹窗；不会留下失去所有者的窗口或悬挂任务。
- 主窗口关闭或 Dispatcher 开始关停时，弹窗层 `Shutdown()` 以 `Closed` 完成所有打开中的弹窗；
  引擎内部等待不依赖 UI 同步上下文，避免关停期间续体被丢弃造成调用方永久等待。

### 7.2 Output 管线

- 三个内存阶段都有固定上限：日志观察缓冲 2000 条、待刷新队列 1000 条、UI 可见集合 500 条。
- 所有阶段都采用“丢弃最旧”策略；`AppLogBuffer.DroppedCount`、pending 丢弃数与可见截断数汇总为
  `DroppedEntries`，面板以“已丢弃较早的 N 行输出”提示，压力下不会静默丢日志。
- 清空语义按“输出代次”线性化：`AppLogBuffer.BeginOutputGeneration()` 在与日志提交相同的锁区间内推进代次并
  重置截断计数，每条 `AppLogNotification` 携带它提交时的代次。`ClearOutput` 在 UI 线程按
  “清空待刷新队列 → 推进代次 → 在队列锁内推进代次下限 → 发布下限 → 清空可见集合”执行。
- 待刷新队列（`OutputBuffer`）自己维护单调递增的代次下限，并与队列、丢弃计数共用一把锁：
  代次低于下限的条目在入队处直接拒绝，不占容量、不增加丢弃数；下限推进时清理旧代次条目；
  丢弃数按代次归属，旧代次的丢弃数随下限推进清除，新代次的真实丢弃数保留。
  ViewModel 的 Volatile 下限检查只是快速过滤，不是唯一防线。
- 事件准入、队列入队、刷新调度、跨线程清空调度与 `Dispose` 共用生命周期锁：`Dispose` 返回后不会再有
  新的有效入队或新的 Dispatcher 操作；跨线程 `ClearOutput` 的 `DispatcherOperation` 在锁内提交并保存句柄，
  尚未开始的刷新与清空操作都会被取消；已经在 UI 线程执行的回调由 Dispatcher 屏障等待完成。
  可见集合只在拥有该 Dispatcher 的 UI 线程上修改。
- 面板释放时退订日志事件、清空队列并停止刷新；日志缓冲释放后不再接收新行。

### 7.3 Workspace 状态通知

- `IWorkspaceHost` 继承 `IWorkspaceChangeSource`；`Subscribe` 返回可释放令牌，通知包含
  `WorkspaceId`、变化种类（生命周期 / 脏状态）与变化前后的值。
- 通知在状态提交之后、于引起变化的线程上同步发出，按订阅顺序调用；订阅者异常被记录并隔离，
  不会回滚状态迁移或阻止其他订阅者。Core 不依赖 WPF。
- Shell 通过订阅更新标签脏标记与活动工作区的 Inspector，不轮询，也不复制权威状态。

## 8. 已知限制

- 无活动工作区时中央区域显示空状态；关闭全部标签后需要从导航重新打开。
- 页面内部布局尚未按 ADR 0050 逐页重构；窄窗口下需要折叠导航或 Inspector 才能获得更宽内容区。
- B0 只提供 `PageHeader`、`PageStateView` 与 `ToolbarButton`；页面在 B1 起逐步接入，尚未接入的页面仍使用各自布局。
- 实例工作区仍共享全局 `SelectedInstance`（C-01）；状态隔离与 `instance:v1:` 身份在 B2 实施。
- 右侧仍是单一 Inspector 面板；ADR 0056 的 Right Auxiliary Rail（Inspector / Tools 双模式）尚未实现。
- 工作区页面在切换标签时会重新创建视图（ViewModel 状态保留，滚动位置等视图状态不保留）。
- Split、Detached、Snapshot 恢复与运行时隔离仍未实现，相关能力不会以按钮或菜单形式出现。
- WPF 线程/Dispatcher、共享控件与输出管线测试位于仅 Windows 可运行的 `tests/ECL.App.Tests`；CI 在 Windows job 执行，
  Linux job 不运行该测试项目。
