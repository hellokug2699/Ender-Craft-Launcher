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

## 2. 导航与工作区

- 全局导航只选择高层目的地，不再承担标签或布局职责。
- 每个目的地对应一个工具标识（`ShellNavigationItem.ToolId`），点击后在中央 Workspace Host 打开或复用该工作区。
- 页面 ViewModel 仍由 `App.xaml` 的 `DataTemplate` 映射为视图；工作区只切换内容，不直接构造视图。
- 实例工作区以 `ToolId = "instance"` 和实例 ID 作为目标资源，同一实例不会重复打开第二个标签。
- 切换标签只改变活动工作区，不销毁页面 ViewModel；关闭标签才会移除工作区。
- 导航高亮跟随活动工作区；实例工作区没有独立导航项，因此导航不显示选中态。

工作区身份、生命周期与关闭判定位于 UI 无关的 `ECL.Core.Workspace`：

| 类型 | 职责 |
| --- | --- |
| `WorkspaceId` / `WorkspaceDescriptor` | 稳定标识、工具标识与目标资源引用 |
| `WorkspaceLifecycleState` / `WorkspaceEntry` | `Created → Opening → Ready → Suspended → Closing → Disposed` 及 `Crashed` / `Unavailable` |
| `WorkspaceLayout` / `WorkspaceLayoutNode` | 不可变 Split Tree：分割深度 ≤ 2、同时可见 ≤ 4、比例限定在 0.2–0.8、关闭后折叠空节点 |
| `WorkspaceHost` | 登记、激活、挂起、异常状态、关闭与可见布局 |
| `IWorkspaceCloseGuard` / `DefaultWorkspaceCloseGuard` | 未保存状态需要用户确认；已关闭状态拒绝重复关闭 |

对应测试位于 `tests/ECL.Tests/Workspace/`，覆盖深度与数量上限、非法节点、关闭折叠、脏状态关闭确认、
生命周期迁移与布局引用校验。

### 尚未实现（有明确扩展点）

- **Split 交互**：布局模型与约束已经实现并测试，但 Shell 目前只以单工作区（标签）方式呈现；
  分割按钮、拖拽比例和失败提示尚未提供，因此 UI 不展示任何“分割”控件。
- **Detached Window**：没有独立窗口生命周期。
- **Session Snapshot / 启动恢复询问**：尚未持久化布局或工作区状态；启动仍进入默认目的地。
- **Addition Runtime / Native UI Host / IPC / Event Bus / Resource Broker / Document Service**：不属于本批范围。

## 3. Inspector

Inspector 只投影活动工作区的宿主元数据（标题、工具、生命周期、目标资源、打开时间、未保存标记），
并提供“关闭工作区”这一真实动作（仍走 Close Guard）。没有活动工作区时显示空状态。
它不复制页面内容，也不持有任何业务数据或独立导航能力。

## 4. Output / Task 面板

- 任务视图直接复用既有 `OperationCenterViewModel`（`IOperationStore` 快照）；没有第二套任务登记。
- 输出视图读取 `AppLogBuffer`：固定容量 2000 行、UI 最多显示 500 行，按日志级别着色，支持清空。
  它只是应用级日志观察窗，不替代文件日志或游戏进程日志会话。
- 面板开合、切换标签、收起都不启动、暂停、取消或释放任何任务。

## 5. Dialog Engine

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

## 6. 线程与状态通知契约

### 6.1 Dialog Engine

- `DialogLayerViewModel`、`DialogEntryViewModel` 与所有绑定属性只归创建它们的 WPF Dispatcher 所有；
  在错误线程上构造或修改会抛出 `DialogThreadAffinityException`，而不是静默跨线程写绑定状态。
- `ShowAsync` 可从任意线程调用：后台调用会被封送到 UI 线程创建弹窗，并返回同一个结果任务。
  `Show` 使用嵌套消息循环实现同步模态，只能在 UI 线程调用。
- CancellationToken 回调可能在线程池执行；引擎把关闭请求封送到 UI 线程，重复关闭/取消竞争由 UI 线程串行化，
  每个弹窗任务只完成一次。
- 关闭父弹窗时，仍打开的嵌套子弹窗先按 `Closed` 完成，再完成父弹窗；不会留下失去所有者的窗口或悬挂任务。
- 主窗口关闭或 Dispatcher 开始关停时，弹窗层 `Shutdown()` 以 `Closed` 完成所有打开中的弹窗；
  引擎内部等待不依赖 UI 同步上下文，避免关停期间续体被丢弃造成调用方永久等待。

### 6.2 Output 管线

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
- 事件准入、队列入队、刷新调度与 `Dispose` 共用生命周期锁：`Dispose` 返回后不会再有新的有效入队或
  新的 Dispatcher 调度；已提交但未开始的刷新操作会被取消，已经在 UI 线程执行的刷新回调由 Dispatcher
  屏障等待完成。可见集合只在拥有该 Dispatcher 的 UI 线程上修改。
- 面板释放时退订日志事件、清空队列并停止刷新；日志缓冲释放后不再接收新行。

### 6.3 Workspace 状态通知

- `IWorkspaceHost` 继承 `IWorkspaceChangeSource`；`Subscribe` 返回可释放令牌，通知包含
  `WorkspaceId`、变化种类（生命周期 / 脏状态）与变化前后的值。
- 通知在状态提交之后、于引起变化的线程上同步发出，按订阅顺序调用；订阅者异常被记录并隔离，
  不会回滚状态迁移或阻止其他订阅者。Core 不依赖 WPF。
- Shell 通过订阅更新标签脏标记与活动工作区的 Inspector，不轮询，也不复制权威状态。

## 7. 已知限制

- 无活动工作区时中央区域显示空状态；关闭全部标签后需要从导航重新打开。
- 页面内部布局尚未按 ADR 0050 逐页重构；窄窗口下需要折叠导航或 Inspector 才能获得更宽内容区。
- 工作区页面在切换标签时会重新创建视图（ViewModel 状态保留，滚动位置等视图状态不保留）。
- Split、Detached、Snapshot 恢复与运行时隔离仍未实现，相关能力不会以按钮或菜单形式出现。
- WPF 线程/Dispatcher 与输出管线测试位于仅 Windows 可运行的 `tests/ECL.App.Tests`；CI 在 Windows job 执行，
  Linux job 不运行该测试项目。
