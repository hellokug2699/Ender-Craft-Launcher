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
- 实例工作区的页面对象持有 `InstanceWorkspaceState`（打开时捕获的实例快照）；工作区关闭时由 Shell 释放。
- 切换标签只改变活动工作区，不销毁页面 ViewModel；关闭标签才会移除工作区。
- 导航高亮跟随活动工作区；实例工作区没有独立导航项，因此导航不显示选中态。

### 2.1 工作区身份与去重（B0）

工作区身份固定为 `(ToolId, TargetResource)`，并且只有一个实现：

| 类型 | 职责 |
| --- | --- |
| `WorkspaceTarget` | 强类型目标：`Kind`（`Destination` / `Instance`）、`SchemaVersion`、稳定实例标识 |
| `WorkspaceTargetSerializer` | 唯一的版本化序列化器：语法校验、规范化、旧值兼容；**不做**文件授权与路径边界校验 |
| `WorkspaceTargetCompatibility` | 裸实例 ID 与 `instance:<id>` 的一次性规范化与等价判断 |
| `WorkspaceToolId` | ToolId 语法：全小写 ASCII，`tool.<name>` 只允许一段 |
| `WorkspaceDedupKey` | 去重键 `(ToolId, 规范化 TargetResource)`，Ordinal 比较 |

- 目的地目标序列化为 `null`；实例目标序列化为 `instance:v1:<stableInstanceId>`。
- 裸实例 ID 与旧 `instance:<id>` 在解析时一次性规范化，同一会话不会同时存在两种等价标签。
- `file:` / `world:` 目标在其消费者批次引入；当前构建遇到这些前缀会显式拒绝，而不是退化成任意字符串。
- 版本号高于当前构建的实例目标（如 `instance:v2:`）被显式拒绝，不会退化为任意路径访问。
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

### 2.3 实例工作区二级导航（B2 步骤 2）

- 唯一权威清单是 `hints/ui/pages/instance-workspace.md` §10。代码中的分组、顺序与默认页只有一个构造点
  （`InstanceWorkspaceNavigationFactory`），测试 `tests/ECL.App.Tests/Workspace/InstanceWorkspaceNavigationTests.cs`
  逐项核对条目 Key、分组顺序、每组条目数与默认页，防止实现自行增删导航。
- `InstanceDetailPageViewModel` 现在是实例工作区壳：左侧纵向二级导航 + 右侧当前二级页。切换二级页只改变
  `Navigation.CurrentPage`，不新开工作区、不改变工作区标题（迁移规划 §5.1）。已实现页面为
  「基本信息」（`InstanceBasicInfoViewModel`，默认页）、「配置 · 修改」
  （`InstanceModifyViewModel`，B2 步骤 3b-4a 只读计划）与「内容 · 模组」（`ModsViewModel`），
  三者各自拥有唯一的滚动容器，由 `App.xaml` 的 DataTemplate 映射到视图。
- 尚未实现的页面（资源包、光影包、数据包、截图、投影、导出、日志）只保留不可用导航项：
  `IsEnabled=false`、UIA `HelpText` 与可见的「未实现」徽标共同说明原因，不创建空页、不提供可点击入口；
  日志项单独说明 G-01 页面规格尚未批准。不可用项不会被代码或键盘路径激活（`TryActivate` 与选中 setter 双重拒绝）。
- 窄窗口收缩（ADR 0050）：工作区页面可用宽度小于 640 时，左侧二级导航折叠为内容区顶部的页面选择器；
  1080×720 且全局导航与 Inspector 同时展开时，内容区仍保持可用宽度，恢复宽度后回到纵向导航。
- 打开工作区时同时初始化实例运行设置与模组内容。初始化失败由 Shell 记录警告，不产生未观察的后台任务；
  模组扫描与 Java 状态更新在页面释放后不再回写已释放状态。
- 基本信息顶部已按 `instance-workspace.md` §2 改为 2:1 布局（B2 步骤 3a）：左侧实例信息与操作，
  右侧 Java 方块。方块标题/发行版/完整版本来自真实运行时，背景与右下角标识映射领域兼容性结论：
  紫色/✓ 兼容、红色/X 不兼容（含 32 位拒绝）、黄色/⚠ 仅在实例偏好抑制常规警告时为黄色
  （该偏好属 B2 步骤 4）、未知为中性色与 ?，不得伪装成兼容。方块内可选择实例 Java，
  「选择 Java… / 下载 Java / 刷新」保留原有能力；任务栏等无障碍名称已补齐。
- 基本信息「高级选项」已重写（B2 步骤 3b-1）：三段式内存条（蓝=系统已用、绿/红=游戏预计占用、
  灰=剩余）与「最大/已用/分配」文本；超出最大内存时游戏段转红并显示独立提示。系统内存与压力分级
  共用 `IMemoryPressureMonitor` 的一次采样；读取不到时显示「不可用」而不是 0。
- JVM 参数恢复可编辑（`MonoInputTextBox`；`MonoTextBox` 仍只用于只读命令预览），默认显示
  「遵循全局设置」水印；新增「还原」仅清理本实例 `extraArguments/jvmPreset/customJvmPreset`。
  逐参数黄/红标记与内存过少/过大阈值仍缺产品定义，本批未自行设定。
- 基本信息「存储空间」卡片已接入（B2 步骤 3b-2）：`StorageAnalysisService.MeasureInstanceAsync`
  按实例游戏目录测量，复用存储中心的 `StorageUsageStatus`（已扫描/估算/不可用）与文件数口径；
  未知或不可访问不显示为 0。「管理存储空间」跳到存储中心并定位当前实例
  （`StorageViewModel.FocusInstance`；存储页首次进入才扫描）；大小文本由 `StorageSizeFormatter` 统一格式。
- 关闭标签时依次释放导航、实例操作、模组、运行设置、存储卡状态与实例快照；每个工作区的导航选中项与页面对象互不共享。
- 模组页表格已按 `mods.md` §5–§7 重写（B2 步骤 3b-3a）：固定五列（启用/状态/文件名/适用版本/工具），
  `ModItemViewModel` 只投影 `ModEntry`；「适用版本」只从 `minecraft` 依赖的版本范围派生，缺失时显示
  「未知」而不是猜测。七种状态由 `ModStatusPresentation` 映射（危险状态保留枚举与渲染，但 ADR 0043 的
  判定尚未接入、当前不产生命中）；多状态全部展示、最多四个，`ModStatusBadgePanel` 按 1/2/3/4 排列，
  `ModStatusGlyph` 绘制矢量图标（冲突/缺少依赖/禁用用禁止与禁行符号，不使用 emoji 字形）。
  四种排序（默认字母正序）、五种单选筛选（危险/可更新在选中时如实说明能力缺口并显示 0 结果）、
  仅英文搜索与「总计 xxx 个模组 / 共 xxx 个结果」计数、批量选择与启用/禁用所选。
  「更新」与「更新所选」因缺少逐模组检查更新 API 保持不可用并在 Tooltip 说明原因。
  表格高度受限、只在表格内部滚动；窄窗口保持最小可读列宽并在表格内部横向滚动。
- 模组页顶部操作与下载上下文已接入（B2 步骤 3b-3b）：第一行「刷新 / 添加模组 / 下载模组」，
  第二行「全选/反选 / 启用所选 / 禁用所选 / 更新所选」（更新所选保持不可用）。
  「下载模组」通过 `ModsPageFactory.OpenModBrowser` → `ShellViewModel` → `DownloadViewModel.OpenModBrowserAsync`
  带入稳定 InstanceId 与当前搜索词，下载页按已有实例模式选中目标实例并自动填充/锁定版本与加载器；
  实例失效时显示 `downloads.instanceMissing` 并要求重新选择，不静默退回自定义筛选。
  「添加模组」的文件选择器只接受 `.jar`；`ModService.ImportAsync` 先确认文件含可识别模组信息、
  再按实例加载器检查适用性，未通过的文件不复制且逐条返回原因。ADR 0043 的逐文件威胁扫描尚无单文件入口，
  本批不做安全判定、也不显示“已检查”；导入后的模组仍受实例级启动安全门控覆盖。
- 模组页旧区块已整理（B2 步骤 3b-3c）：依赖与修复、整合包导入/导出、资源包与光影移入表格下方的
  「高级与依赖」卡片并默认折叠，命令与能力保持不变；资源包/光影在 B2b 独立页落地前保留入口，
  并在区块内说明独立页面尚未开放。资源包行的名称/描述改为单行省略，避免畸形 `pack.mcmeta` 描述撑高列表。
- 「修改」页已按 `instance-modify.md` 落地只读变更计划（B2 步骤 3b-4a）：导航项从不可用改为可用，
  页面顶部固定显示当前实例与目录，中部逐行对照「当前配置 / 拟变更值」，底部提供「生成变更计划」。
  - 目标实例由工作区打开时的稳定 InstanceId 固定绑定；页面不提供目录或实例切换入口，也不创建新实例。
  - 目标版本只来自 `IVersionCatalog`（外部实例版本不在目录中时单独标注，不伪造日期）；加载器按目标版本
    过滤（Cleanroom 仅 1.12.2，选择其它版本时从选项排除并说明）；加载器版本、Fabric API 与 OptiFine
    由 `ILoaderCatalog` 提供。Fabric API 默认目标组合最新兼容版本并明确展示；OptiFine 只在用户显式选择时
    安装，当前版本在目标版本下没有文件时不自动替换，只提示手动选择或移除。
  - 计划条目只来自可证明的领域结论：身份/目录、当前→目标版本与加载器、组件、当前 Java
    （`IJavaSelectionService` + `JavaStatusPresentation`/`JavaSelectionPresentation`）、目标 Java 要求
    （目标版本文件尚未安装时只使用 `JavaRequirements.TryInferMinimumMajor` 的推断值并标注「推断」）、
    模组影响（`ModService.ScanAsync` 的元数据 + `ModVersionRange`/`ModCompatibility`，三档：确定不兼容、
    未知、元数据声明兼容；禁用模组不计）、资源包/光影与数据包（无可靠兼容声明时显示「未知」）、
    需要下载的产物与实际来源（大小一律显示「大小未知」，不猜测字节数）、运行中的游戏进程
    （`IGameSessionService` 会话 + 进程存活校验，过期会话不阻塞）、进行中的写入任务
    （`IOperationService`，任务记录没有 InstanceId，因此明确写「无法证明属于哪个实例」）、
    快照要求与范围、需要手动处理项（遗留 OptiFine / Fabric API 文件、确定不可用的模组）。
  - 仅在目标版本或加载器真正变化时才扫描内容影响；只调整加载器小版本或 OptiFine 时，模组/资源包
    显示「目标版本与加载器未变，兼容性不受本次变更影响」，不把当前已知问题包装成这次变更的影响。
  - 状态机按 §7 实现未检查/检查中/可执行/需要处理/不可执行五种。可执行只在全部强制检查通过时出现；
    失败读取、目标组合缺少加载器版本、缺少 Fabric API、目标加载器需要 processors 但没有可用 Java、
    实例目录不存在时进入不可执行并逐条列出原因；游戏进程或写入任务存在、目标与当前一致时进入需要处理。
  - 本步骤不写入实例、不创建快照、不下载产物：`InstanceModifyViewModel` 的构造依赖里没有任何安装、
    事务或快照服务，提交/执行入口不存在，页面底部写明受控执行属后续步骤（3b-4b）。
  - 生命周期：每工作区一个页面对象，由 `InstanceModifyPageFactory` 创建、`InstanceDetailPageViewModel`
    持有并在关闭标签时释放；目标目录在首次显示页面时读取，选择变化会取消上一次读取，释放后不再回写。
  - 测试：`tests/ECL.App.Tests/Workspace/InstanceModifyPageTests.cs`（当前值投影、版本目录与加载器过滤、
    Cleanroom 限制、无变更、运行进程与过期会话、写入任务冲突、目录缺失、加载器版本/Fabric API 缺失、
    模组三档影响与手动项、Java 推断与不可用、OptiFine 保留策略、下载清单与「大小未知」、快照与目录范围、
    「不调用任何写 API」的结构断言、选择变化使计划失效、释放后异步回写保护、双工作区隔离）+
    `InstanceWorkspaceNavigationTests.ModifyPageBecomesAvailableWhenTheWorkspaceProvidesIt`。
- 仍待 B2：实例级 Java 警告抑制（步骤 4，受 ADR 0014 门禁）、Modify 受控执行（3b-4b：快照 +
    `ReconfigureAsync` + 刷新）与可选组件行（3b-4c）；概览「应用」与实例改名仍为 BLOCKED（见交接文档 §6.1）。

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
- 禁用整个 Shell 时，主题内的 `ListBox` 使用 `Theme.xaml` 的自绘模板：WPF 默认模板会在
  `IsEnabled=false` 时把列表背景换成 `SystemColors.ControlBrush`（浅色），在深色主题下表现为
  「弹窗一出现主页面整块发白」。自绘模板只保留 ScrollViewer + ItemsPresenter，禁用仅影响交互、
  不改变配色；`tests/ECL.App.Tests/Controls/PageControlsTests.cs` 有对应回归测试（渲染禁用列表并断言深色背景）。
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
- 实例工作区二级导航已在 B2 步骤 2 接入（§2.3）：默认基本信息，模组页可用，其余条目为不可用状态；
  基本信息顶部 2:1 与 Java 方块已在 B2 步骤 3a 重写，其余区块仍待后续步骤按 Approved 页面规格完成。
- 实例工作区拥有自己的身份快照（`InstanceWorkspaceState`）：打开时捕获实例条目，标签标题、
  页面身份区块与 Inspector 目标都来自该快照，不再依赖全局 `SelectedInstance` 的更新顺序；
  关闭标签时释放该状态（`IDisposable`），释放后再参与页面状态。
- 实例编辑状态已按工作区作用域拆分（B2 步骤 1c）：`InstanceRuntimeSettingsViewModel` 持有 Java 覆盖、
  内存与 JVM 字段及保存命令，按稳定 InstanceId 读写 `instance.java.*`；`ModsViewModel` 构造时绑定
  唯一 InstanceId，模组、资源包与整合包命令不再读取“当前选中实例”。两者都由
  `InstanceDetailPageViewModel` 持有，关闭标签时释放。Java 选择结果的措辞只有一份实现
  （`JavaSelectionPresentation`），主页启动面板与实例页共用。
- 实例操作状态已按工作区作用域拆分（B2 步骤 1c）：`InstanceOperationsViewModel` 持有本工作区自有的
  `InstanceOperationState`（启动步骤/状态、命令预览、健康报告、崩溃分析、诊断导出、安装进度）与
  启动、健康检查、修复、崩溃分析、诊断/日志导出、打开目录、世界工作台、删除命令。这些命令通过
  `LaunchForAsync` / `CheckHealthForAsync` / `InstallForAsync` / `AnalyzeCrashForAsync` 等
  **以显式实例为参数**的内部方法执行，操作实现只有一份，状态每个工作区一份。
  主页启动面板使用 `InstancesViewModel.Operation`，与实例工作区各自独立。
- 因此 `ShellViewModel.OpenInstanceWorkspace` 不再把打开的实例写成全局 `SelectedInstance`；
  实例列表页与主页的选择同步只由它们自己维护。账户卡片仍是全局账户状态（按页面规划账户属于 Home），
  不属于实例作用域。
- C-01 出口验收（本批证据）：两个实例工作区同时打开时，身份、Java/内存/JVM 编辑状态、模组内容、
  健康检查与启动报告互不覆盖；分别执行健康检查后各自保留自己的报告；关闭其中一个不改变另一个；
  重开工作区得到新的身份与空的操作报告；实例页所有操作按钮保持可用。
- 右侧仍是单一 Inspector 面板；ADR 0056 的 Right Auxiliary Rail（Inspector / Tools 双模式）尚未实现。
- 工作区页面在切换标签时会重新创建视图（ViewModel 状态保留，滚动位置等视图状态不保留）。
- Split、Detached、Snapshot 恢复与运行时隔离仍未实现，相关能力不会以按钮或菜单形式出现。
- WPF 线程/Dispatcher、共享控件与输出管线测试位于仅 Windows 可运行的 `tests/ECL.App.Tests`；CI 在 Windows job 执行，
  Linux job 不运行该测试项目。
