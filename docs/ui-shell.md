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
  （`InstanceModifyViewModel`，B2 步骤 3b-4a 计划 + 3b-4b 受控执行）、「内容 · 模组」（`ModsViewModel`）、
  「内容 · 资源包」与「内容 · 光影包」（B2b，共用 `ContentPackPageViewModel`）、
  「内容 · 数据包」（`InstanceDataPacksViewModel`，B2 收尾，见 §2.10）、
  「世界与文件」下的「截图」「投影」「导出」（B2c，见 §2.5），
  各自拥有唯一的滚动容器，由 `App.xaml` 的 DataTemplate 映射到视图。
- 尚未实现的页面（日志）只保留不可用导航项：
  `IsEnabled=false`、UIA `HelpText` 与可见的「未实现」徽标共同说明原因，不创建空页、不提供可点击入口；
  日志页规格（G-01）已由 Approved 的 `hints/ui/pages/logs.md` 解除，页面本身属 B6，在实现前只说明「尚未实现」。
  不可用项不会被代码或键盘路径激活（`TryActivate` 与选中 setter 双重拒绝）。
- 窄窗口收缩（ADR 0050）：工作区页面可用宽度小于 640 时，左侧二级导航折叠为内容区顶部的页面选择器；
  1080×720 且全局导航与 Inspector 同时展开时，内容区仍保持可用宽度，恢复宽度后回到纵向导航。
- 打开工作区时初始化实例运行设置、模组、资源包/光影、数据包世界清单、截图与投影页面；导出页只在首次显示时扫描
  （避免打开工作区就遍历整个实例目录）。初始化失败由 Shell 记录警告，不产生未观察的后台任务；
  模组扫描、Java 状态与各页面扫描在页面释放后不再回写已释放状态。
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
  「高级与依赖」卡片并默认折叠，命令与能力保持不变；B2b 独立页落地后，资源包/光影区块已从模组页移除，
  避免第二套入口（其余依赖与整合包区块保留）。
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
  - 生命周期：每工作区一个页面对象，由 `InstanceModifyPageFactory` 创建、`InstanceDetailPageViewModel`
    持有并在关闭标签时释放；目标目录在首次显示页面时读取，选择变化会取消上一次读取，释放后不再回写。
- 「修改」页已按 `instance-modify.md` 落地受控执行（B2 步骤 3b-4b）：在 3b-4a 的只读计划与目标值之上
  追加确认 → 快照 → 安装 → 结果四段，不另建第二套选择器或第二套计划。
  - 提交入口只在「计划可执行 + 当前目标与生成计划时完全一致 + 不在执行/恢复中」时可用
    （`ModifyExecutionTarget` 记录生成计划时的目标组合；任何选择变化都会把计划重置为未检查）。
  - 确认走 Dialog Engine（`ModifyConfirmDialogViewModel`）：列出目标版本/加载器/组件、影响四档计数、
    完整实例快照范围与「关闭页面不会取消任务、不提供强制取消」的说明；初始焦点在取消按钮，
    取消不写入任何内容。无法显示确认时不执行，绝不在未确认的情况下写实例。
  - 执行前重新核对运行中的游戏进程与写入任务（计划可能已过期），状态变化则回到「需要处理」。
  - 变更前通过 `IInstanceSnapshotService.CreateAsync(instanceId, Full)` 创建完整快照，再用 `VerifyAsync`
    校验；快照失败/不完整立即停止（「执行前中止」），不开始安装。快照进度通过
    `OperationProgressBridge` 登记为统一 Operation（Kind=Backup），不建第二套任务列表。
  - 安装走 `IVersionInstallService.ReconfigureAsync`（安装服务自己登记 `OperationKind.Install`），
    页面只转发真实阶段文本与结果，不重复登记任务。页面不提供强制取消；关闭页面不会取消任务
    （页面只停止回写自己的状态），是否可取消由统一 Operation 与事务阶段决定。
  - 领域侧新增可验证的回滚信号：`InstallTransaction.Rollback()` 返回 `InstallRollbackResult`
    （`Actions` + `Failures`），`ReconfigureAsync` 返回 `InstallOutcome`，失败时携带
    `InstallFailure(Error, Rollback, MetadataRestored, DiagnosticPath)`。`RecoveryRequired` 只在
    回滚有失败项或实例元数据未还原时为真；UI 只据此显示「失败且已回滚」或「失败且需要恢复」，
    没有信号时按「无法确认」处理，不得宣称已回滚。
  - `ReconfigureAsync` 失败时用 `IInstanceService.RestoreConfigurationAsync(original)` 原样写回变更前的
    实例记录（含 `VersionId` 与时间戳），不再重新推导版本 ID——外部导入实例的 `VersionId` 不是
    `BuildVersionId` 派生值，旧实现会把回滚后的记录改成不存在的版本目录。
  - 状态机在 3b-4a 的五态之外补齐执行中/成功/失败且已回滚/失败且需要恢复，并增加「执行前中止」
    （快照失败、校验不通过等尚未写入的情形）。成功状态显示实际提交的目标组合与已校验快照 ID；
    失败且需要恢复时显示问题项数、快照 ID 与受控恢复入口（`RestoreAsync` 会先创建安全快照），
    诊断入口只在诊断文件真实存在时出现，且页面不显示本地路径。
  - 成功后刷新工作区（`InstancesViewModel.ReloadInstanceAsync` → `InstanceWorkspaceState.ReplaceItem`
    → 基本信息身份文本 + `InstanceRuntimeSettingsViewModel.RebindInstance` 重新评估 Java + 存储卡片重测），
    InstanceId 不变；`InstancesViewModel` 只替换目标实例的列表项，不清空其它卡片或当前选择。
  - 测试：`tests/ECL.App.Tests/Workspace/InstanceModifyPageTests.cs` 覆盖确认前不写入、快照先于安装、
    `Full` 范围与 `Verify` 调用、成功刷新、回滚完整/需要恢复两种失败、快照失败与不完整快照拒绝、
    晚出现的游戏进程阻止执行、目标变化使提交失效、执行中拒绝重复提交、真实安装阶段转发、
    关闭页面不取消任务与不回写；`InstanceWorkspaceStateTests` 覆盖同一实例换快照、拒绝换实例/已释放状态。
    领域侧 `tests/ECL.Tests/Install/InstallServiceTests.cs` 覆盖回滚失败项、失败后实例记录（含 VersionId）
    原样恢复、成功提交保持 InstanceId，以及 `InstallTransaction.Rollback()` 在文件被占用时报告失败项。
  - 实机（1500×950/1080×720、zh-CN/en-US）：临时实例原版 1.20.1 → Fabric，真实创建并校验快照、
    真实安装、成功后工作区身份刷新；取消确认不产生快照；仅加载器版本变化时内容影响显示「不受本次
    变更影响」。失败/恢复路径只由自动化测试覆盖（未能可靠制造一次真实的后写入失败），实机标记 NOT RUN。
- 「修改」页附加组件行已按 3b-4c 收窄（`InstanceModifyViewModel.ShowComponentsSection` /
  `ShowFabricApiRow` / `ShowOptiFineRow` / `ShowOptiFineControl`）：只有目标组合实际支持且 ECL 识别的
  组件才显示。Fabric 目标只读展示默认最新兼容的 Fabric API 版本（保留当前兼容版本），不提供无依据的
  版本输入；Forge/Cleanroom 才显示 OptiFine 选择器，目标版本没有可选文件时改为只读结论 + 真实原因；
  原版/NeoForge 等不支持组合隐藏整个组件区域。已有组件的影响仍由变更计划的「需要处理」项列出，
  不会因隐藏控件而静默丢弃。
- 仍待 B2：实例级 Java 警告抑制（步骤 4，受 ADR 0014 门禁）；
  概览「应用」与实例改名仍为 BLOCKED（见交接文档 §6.1）。

### 2.4 资源包与光影包页（B2b）

页面规格：`hints/ui/pages/resource-library-template.md`（模板）与 `hints/ui/pages/shaders.md`（光影补充）。
资源包与光影包共用一个页面对象与视图（`ContentPackPageViewModel` / `InstanceContentPacksView`），
按资源类型替换标题、数据与配置目标；不复制领域规则。

- 顶部操作：刷新 / 添加 xxx / 下载 xxx。「添加」走文件选择器（`*.zip`）与 `IContentPackService.ImportAsync`；
  「下载」经 `ContentPackPageFactory.OpenPackBrowser` → `ShellViewModel` → `DownloadViewModel.OpenContentBrowserAsync`
  切到下载中心的资源包/光影包页签并带入稳定 InstanceId（下载页的“已有实例模式”可见开关仍属 B4）。
- 双表与计数：已启用 / 未启用两张表格，各自在表格内部左下角显示「共 x 个」，
  不合并为总数；空表显示各自的空状态，不显示 0 条占位。
- 行字段固定为状态、名称及简介、版本范围、功能：名称取文件名（包内没有独立名称字段），
  简介只显示 `pack.mcmeta` 的真实描述（缺失时不显示占位）；版本范围只显示包内声明的
  `supported_formats` 或 `ResourcePackFormats` 官方对照表中的事实，未知时显示「未知」，不按经验外推。
- 状态语义（图标 + 颜色，沿用模组页的矢量图标风格，`ContentPackStatusGlyph`）：绿色=正常/已启用、
  黄色=游戏记录为不兼容（`incompatibleResourcePacks`）、红色=压缩包损坏、
  灰色=未识别（资源包缺 `pack.mcmeta`，光影包缺 `shaders/`）。
- 拖动与启用：同表拖动调整顺序，跨表拖动启用/禁用；拖动只修改页面工作副本，不写盘。
  每行另有「启用/禁用」按钮作为等价的键盘可达操作；光影包一次只能启用一个，
  启用新条目时原条目自动退回未启用表。
- 「应用」位于已启用表格右上、表格外：无待应用更改时为灰色，有待应用更改时为绿色；
  点击后调用 `IContentPackService.ApplyAsync` 一次写入启用状态与已启用顺序
  （资源包重写 `resourcePacks` 的受管条目，保留 `vanilla` 与其它启动器写入的裸值位置；
  光影包写入单个 `shaderPack`）。应用失败时保留用户的待应用更改，不静默丢弃。
- 删除按自身流程执行（确认后立即删除并同步清理选项引用），不进入待应用状态。
- 未启用条目的顺序在 Minecraft 配置中没有表示，因此只作为本页面的整理顺序，
  页面在表格下方明确说明「不写入游戏配置」；只有已启用条目的顺序会被应用。
- 光影支持检测（`ShaderSupportDetector`）只依据实例的 Minecraft 版本/加载器、已启用模组的真实元数据
  （平台、`minecraft` 版本范围、必需依赖）与实例记录的 OptiFine 构建，按
  `docs/shader-support/rules.json` 返回支持/缺失/不完整/不兼容/冲突/未知；Iris 在 Forge 上判为不兼容、
  Oculus 只接受其离散版本集合、Iris 自带 Sodium 时不额外要求 Sodium、无法证明时返回未知。
  检测失败显示失败说明，不降级为「支持」。
- 无支持组件时的提示（`shaders.md` §3–§5）：首次进入显示非模态页面横幅（标题/说明/具体原因/
  「仍可导入或下载光影包」），用户收起后按实例保存（`config/shader-hints.json`），
  后续进入显示一行灰色弱提示；删除全部光影包后重置该实例状态，下一次进入且仍不支持时重新显示横幅；
  检测恢复为支持时两种提示立即隐藏。提示状态不写入实例目录。
- 测试：`tests/ECL.Tests/Minecraft/ContentPackServiceTests.cs` 覆盖 `ApplyAsync` 的顺序重写、
  保留未受管条目、光影单选、缺失文件/不可解析 options 拒绝、损坏/未识别状态与声明式格式范围；
  `tests/ECL.Tests/Minecraft/ShaderSupportDetectorTests.cs` 覆盖六种检测状态与「未知不算支持」；
  `tests/ECL.App.Tests/Workspace/ContentPacksPageTests.cs` 覆盖双表与计数、拖动后的待应用状态、
  按钮等价操作、光影单选、删除确认、状态映射、版本范围文案、不可读配置、释放后停止扫描，
  以及光影横幅/灰色提示/清空重置/检测失败。
- 实机（1500×950、1080×720，zh-CN/en-US）：临时实例（`instance create` 新建、验收后删除）上
  拖动/按钮启用 → 应用写入 `resourcePacks:["vanilla","file/B2b Demo Pack.zip"]` 且 `.bak` 保留旧文件；
  光影包启用/切换后 `shaderPack` 始终只有一个；收起横幅后跨页切换显示灰色提示；
  清空全部光影包并刷新后横幅重新出现；「下载光影包」打开下载中心光影包页签并带入实例目标。

### 2.5 截图、投影与导出页（B2c）

页面规格：`hints/ui/pages/screenshots.md`、`schematics.md`、`export.md`。三个页面分别拥有页面对象与视图
（`ScreenshotPageViewModel` / `InstanceScreenshotsView`、`SchematicPageViewModel` / `InstanceSchematicsView`、
`InstanceExportViewModel` / `InstanceExportView`），由 `InstanceDetailPageViewModel` 持有并在关闭标签时释放；
导航项在页面对象存在时可用，不创建空页。

- 截图（`IScreenshotService`）：只读取实例游戏目录 `screenshots/` 下的直接图片文件，扩展名之外还按 PNG/JPEG
  签名确认；读取有字节上限，路径固定为 `screenshots/<文件名>`，UI 不拼接文件路径。
  - 三种展示形式共享同一组条目与选择：列表（每行一张，缩略图/文件名/时间/大小/复选框）、
    方格（每行五张，图片下方信息，右下角复选框）、极简（每行八张，无信息与复选框）。
    切换形式不改变文件，也不丢失已选条目；支持时间正序/倒序。
  - 缩略图在行真正显示时才按需读取并解码（`IScreenshotImageLoader`，320px 宽），同一行只请求一次；
    文件签名不是图片时显示几何占位图，不伪造截图。解码失败显示失败状态而非空白成功图。
  - 点击图片在当前页面内打开覆盖层预览，支持上一张/下一张、Esc 关闭与左右方向键，不新建工作区或独立页面。
  - 删除按钮位于图片区域之外的标题行右侧：确认框显示张数与总大小，删除经
    `InstanceTransactionCoordinator` 在一个实例事务中执行；失败时保留列表与选择，不显示部分成功。
  - 1080×720 下工具条自动换行，列表在表格内部横向滚动（最小可读宽度 520），页面本身不横向溢出。
- 投影（`ISchematicService` / `ISchematicSupportService`）：列表式管理 `schematics/`（Litematica/Create/MCEdit）、
  `config/worldedit/schematics/` 与 `config/axiom/blueprints/` 中的已知格式；
  `.litematic/.schem/.schematic/.nbt` 需匹配 gzip 或未压缩 NBT 容器签名，`.bp` 只按扩展名识别并标记
  「格式未验证」，不解析或伪造内容。每行显示类型徽标、文件名、格式、时间、大小与选择；无三维缩略图。
  - 支持检测按 `docs/schematic-support/`（`README.md` + `rules.json`）只用实例版本/加载器、已启用组件的真实
    元数据与必需依赖判定，返回支持/缺失/不完整/不兼容/冲突/未知，并逐格式展示；多个兼容组件声称同一格式时
    显示冲突，元数据不足显示未知，不猜测支持。检测不阻断文件管理。
  - 「查看工具」明确显示不可用：NBT Explorer 需要独立 Addition（G-03 未提供安装/文件处理器入口），
    三维预览需要投影渲染 Addition；不放置假按钮或伪造预览。
  - 删除按钮在列表区域外，确认框显示文件数与总大小；删除经实例事务执行。1080×720 下列表内部横向滚动。
- 导出（`IInstanceExportService`）：完整导出 / 自定义导出 / 诊断包三种方式共用页面。导出包固定写入实例
  游戏目录的 `exports/`，不提供自定义保存位置；页面不直接写文件。
  - 首次显示页面时才扫描实例内容（`PlanAsync`），按世界存档、截图、投影、日志与崩溃报告、配置、模组、
    资源包、光影包、其他实例文件分类，显示真实文件数与未压缩体积估算；未知体积显示未知，不写 0。
  - 自定义导出可勾选类别；风险提示跟随类别显示（截图、投影、日志、配置、世界存档），完整导出的确认框
    汇总所选类别的风险。开始前通过 Dialog Engine 确认模式、文件数与估算大小。
  - 永久排除登录状态、账号认证信息、访问/刷新令牌、API Key、私钥及等价凭据；排除项属于引擎硬边界，
    不受类别勾选影响，并写入 `manifest.json` 的 `permanentExclusions`。ECL 身份标记 `.ecl/` 与 `exports/`
    自身不进入包内（实例元数据以 `instance/instance.json`、`instance/settings.json` 单独携带）。
  - 进度复用 `IOperationService`（Kind=Export）与统一进度条：扫描/打包/完成阶段，字节与文件数分开；
    成功绿色、失败红色、部分跳过黄色。任务关闭页面后继续，页面只停止回写自身状态。
  - 导出结果为 zip：`manifest.json` + `instance/` + `minecraft/`；逐文件报告跳过原因（不可读、读取期间变化、
    元数据不可读），任何跳过都会使结果标记为不完整并在页面列出，不伪装成完整成功。成功后显示路径、
    最终大小与「打开所在位置」；诊断包复用 `IDiagnosticPackageService`，默认排除存档、截图、投影与凭据。
  - 测试：`tests/ECL.Tests/Minecraft/ScreenshotServiceTests.cs`、`SchematicServiceTests.cs`、
    `SchematicSupportDetectorTests.cs`、`InstanceExportServiceTests.cs`；应用侧
    `ScreenshotPageTests.cs`、`SchematicPageTests.cs`、`ExportPageTests.cs` 覆盖列表/选择/删除/预览/支持状态/
    导出模式/风险确认/估算/跳过与释放。实机见交接文档 §4（1500×950、1080×720、zh-CN/en-US）。

### 2.6 下载中心与实例版本页（B4a-1）

页面规格：`hints/ui/pages/download-instance-versions.md`。下载中心仍是主导航的 `download` 目的地，
横向二级目录的唯一构造点是 `DownloadCategories.Create`（`DownloadViewModel` 同一文件），顺序固定为
**实例版本、模组、整合包、资源包、光影包、数据包**；「游戏实例」更名为「实例版本」。
二级页切换只清空内容发现区的结果，不重置实例版本页已配置的选择。

- **实例版本页**（`InstanceVersionPageViewModel` + `Views/InstanceVersionPageView.xaml`，
  经 `App.xaml` 的 DataTemplate 嵌在 `DownloadView` 内）：
  - 第一行：游戏版本（只能从版本清单选择，不提供手输）、类型（正式版/快照版/愚人节版）、
    实例名称、安装位置。第二行为加载器（无/Forge/NeoForge/Fabric/Cleanroom，选择项显示当前加载器
    版本号）与加载器版本（版本 · 发布日期，来自 `ILoaderCatalog`）。
  - Fabric 目标显示必装的 Fabric API 行（版本来自目录，可展开选择历史版本）与灰色背景提示
    “安装Fabric必须同时安装Fabric api才能正常加载模组”；Forge/Cleanroom 目标显示 OptiFine 行，
    默认「不安装」，只有用户显式选择才安装；其他组合不显示这两行，不渲染空控件。
  - 每个下载项（Minecraft 游戏文件 / 加载器 / Fabric API / OptiFine）在卡片下方有一条状态线：
    已完成绿、正在下载白、正在排队灰、下载失败红；未开始不画线，未参与本次部署的项显示「未选择」
    并灰化。状态只来自安装服务真实上报的 `InstallStage`（Manifest/Version → Loader/Processors →
    Client/Libraries/Assets/Logging → FabricApi → OptiFine），不虚构阶段。
  - 总进度使用线性进度条与阶段文本；安装阶段有可靠字节总量时按字节计算百分比，否则退回文件计数，
    再没有计数时只显示阶段文本（不伪造百分比）。文件大小来自真实元数据：Minecraft 游戏文件由
    `IVersionDownloadSizeEstimator` 汇总官方版本 JSON，Fabric API 用目录字节数，整合包归档用来源/
    实际归档大小；缺少可靠来源的加载器仍显示「大小未知」，不估算、不写 0。详见 §2.8。
  - 「开始下载」调用 `IInstanceService.CreateAsync` 后立即用 `IVersionInstallService.InstallAsync`
    部署同一个新实例；创建失败不进入安装，失败时保留已创建的实例并显示红色「查看错误信息」
    与「重试失败的下载项」（重试只重跑安装，靠领域哈希校验续传/重下，不重新创建实例）。
  - 实例名称重复时输入框出现红色边框并在下方显示「实例已存在，请换一个名字或换一个目录」，
    重复名称不可提交；名称留空时沿用既有行为，用游戏版本作为默认名称。
  - 全部下载完成后页面只保留「重新布置」：保留安装位置，其余选项（版本、类型、名称、加载器、
    组件与下载项状态）清空，回到可配置新实例的状态。部署中与成功后所有配置控件禁用，
    因此不可能同时下载两个实例。
  - 页面不因切换二级页或关闭工作区而刷新；`ShellViewModel` 在进入 `download` 目的地时调用
    `DownloadViewModel.ActivateAsync`（迁移规划 C-03 的首次激活），只刷新目标实例与位置登记，
    不重置已配置的选择。
  - UIA/无障碍名称只使用本地化标题（`DownloadTabViewModel`、`LoaderKindOption`、
    `VersionChoiceViewModel` 等改写了 `ToString()`），不暴露记录类型的默认字符串。
- **安装位置**：新建实例固定创建在主工作区（`IAppPaths.GameRoot`）。展开区列出 `IWorkspaceService`
  已登记的所有根目录，主工作区标「当前安装位置」，外部目录标「只读来源」，并明确说明外部 Minecraft
  目录不会接收新实例（ADR 0018 / 0046）；「添加目录」复用实例页面的目录登记弹窗
  （`InstanceDirectoryDialogViewModel`），不另写一套选择逻辑。
- **过渡区已移除（B4b）**：`DownloadView` 不再保留「安装 / 补全已有实例」卡片；既有实例的补全/修复
  由实例工作区基本信息页的「补全/修复」入口承担（`RepairForAsync` → `InstallForAsync`），
  下载页只负责新建实例。对应 `downloads.game.existing*` 键已删除。
- 测试：`tests/ECL.App.Tests/Download/InstanceVersionPageTests.cs`（分类顺序、默认选择、类型过滤、
  Fabric/Forge/NeoForge 条件组件、Cleanroom 限制、同名提交拦截、创建+安装顺序、真实阶段映射、
  失败重试不重复创建、创建失败不安装、成功后必须重新布置、Fabric API 参数、版本目录失败、
  安装位置只读来源、真实产物大小、部署自身新建实例不算重名、字节百分比）。实机见交接文档 §4
  （1500×950、1080×720、zh-CN/en-US）。

### 2.7 下载中心内容发现页（B4a-2）

页面规格：`download-mods.md`、`download-modpacks.md`、`download-resource-template.md`。
模组、整合包、资源包、光影包与数据包共用页面对象 `ContentDiscoveryViewModel`
（`ViewModels/ContentDiscoveryViewModel.cs`）与视图 `Views/ContentDiscoveryView.xaml`；
`ContentDiscoveryPageFactory` 为每个分类创建独立页面对象，`DownloadViewModel` 以 `ContentPages`
持有并按二级页切换。数据包分类当时只显示准确的能力阻断说明，后由 §2.9 接通世界目标。

- **第一行**：英文搜索与来源（全部 / CurseForge / Modrinth）。非 ASCII 输入立即标红并显示
  “搜索只支持英文输入”，且不提交；来源切换立即按新来源重新检索，不合并跨来源条目。
- **第二行**：模组页提供“为已有实例下载模组”复选框。勾选后只显示已登记实例选择框，实例的
  Minecraft 版本与加载器成为锁定筛选条件；未勾选时显示 Minecraft 版本与加载器筛选
  （无 / Forge·Cleanroom / Fabric / NeoForge，“无”表示不限制加载器）。整合包页只有版本与
  加载器筛选，并明确说明整合包始终创建新实例；资源包 / 光影包固定要求目标实例。光影页显示
  实例当前光影组件检测结果，并说明单个光影包能否渲染无法从来源元数据确认。
- **列表一行**：图标、名称（3 号文字）、简介（2 号文字）、版本范围、加载器或分类适用信息、
  来源徽标与文件大小；整行是可键盘聚焦的按钮，点击打开该来源的详情弹窗。
- **详情弹窗**（`ContentDetailDialogViewModel` + `ContentDetailDialogView`）：名称、作者、来源链接、
  简介（高度固定、内部滚动）、下载次数与更新时间。有筛选项时上表“筛选的版本”、下表“全部版本”，
  同一版本允许同时出现；无筛选时只显示全部版本。每个版本行显示版本号、文件名、Minecraft 版本
  范围、加载器/适用信息、真实文件大小、发布时间与浅紫色圆形下载按钮；进度在行内以线性进度条
  呈现（白=进行中、绿=完成、红=失败），不使用圆周按钮或卡片边缘动画。来源只返回部分版本时
  （CurseForge 分页上限）明确标注列表不完整。
- **文件大小**：CurseForge / Modrinth 的搜索响应本身不带文件大小。ECL 在搜索后额外做一次批量
  查询（CurseForge `mods/files`、Modrinth `versions?ids=[...]`）取回主要文件真实字节数；查询失败、
  来源未声明或文件被作者禁止分发时保持“大小未知”，不以 0 或估值代替。CurseForge 的
  `Client` / `Server` 环境标签不是 Minecraft 版本，展示适用版本时排除。
- **安装流程**：已有实例目标 → 三按钮确认弹窗（安装 / 安装到其他位置 / 取消）；未选择实例的
  模组 → 先调用 ECL 已有的目录选择能力，再按“即将安装到位置 xxx”确认；“安装到其他位置”打开
  目标选择弹窗（已登记实例；只有模组可再选目录），不会静默改变本次安装目标。所有确认都经
  Dialog Engine，宿主不可用时拒绝开始。
- **领域**：新增 `IContentVersionCatalog`（版本清单）与 `IContentInstallService.InstallVersionAsync` /
  `DownloadVersionAsync`。页面只传来源与版本 ID，文件由来源 API 重新解析（Modrinth 校验
  ProjectId、CurseForge 校验 ModId 与官方 CDN），不接受 UI 拼接的 URL；整合包与数据包在领域
  直接被拒绝并给出准确原因。
- **条级进度**：页面只有版本行内进度，全局任务状态仍由 `IOperationService` 进入 Output / Task
  面板。多个版本行可同时下载，页面按 `network.maxConcurrentDownloads` 排队并显示“正在排队”。
- **能力边界（B4b 更新）**：整合包版本行已接通实例版本页的整合包模式（见 §2.8），本页仍不建立
  第二套安装表单；数据包需要世界目标（ADR 0026 / 0031），该分类当时只显示阻断说明（后由 §2.9 接通）；CurseForge 作者禁止
  第三方分发时下载失败会显示领域原因，不伪造成功。
- 测试：核心 `ContentVersionCatalogTests`（7）与 `ContentSearchTests`（作者、文件大小补全）；
  应用 `ContentDiscoveryPageTests`（目标模式与锁定、英文输入、来源切换、列表字段、详情两表、
  确认 / 取消 / 改目标、目录下载、资源包与光影包目标、失效实例、整合包跳转与数据包边界）。
  实机结果见交接文档 §4。

### 2.8 实例安装归属、结构化依赖搜索与整合包模式（B4b）

页面规格：`download-instance-versions.md` §5、`download-modpacks.md` §8、`instances.md` §5。

- **过渡卡片清理**：删除 `DownloadView` 的「安装 / 补全已有实例」卡片与
  `downloads.game.existing*` 键；删除 `InstancesViewModel` 的页面级 `InstallCommand` 与
  `EnsureJavaForInstanceAsync`。既有实例安装的正式归属是实例工作区的「补全/修复」入口，
  它仍调用同一个 `InstallForAsync`（领域安装服务只有一份）。
- **结构化依赖搜索上下文**：新增 `ContentSearchContext(Kind, InstanceId, GameVersion, Loader,
  Query, ExistingInstance)`。Shell 从已加载实例记录构造上下文（稳定 InstanceId + 版本 + 加载器 +
  搜索词），`DownloadViewModel.OpenContentBrowserAsync(context)` 传到
  `ContentDiscoveryViewModel.ActivateAsync(context)`；已有实例模式的版本/加载器锁定值优先取上下文，
  目标实例仍会重新读取验证，失效时明确要求重新选择，不静默退回自定义筛选。
- **整合包模式入口**：
  - 下载中心整合包分类的版本行下载按钮调用 `OpenModpackDeployment` → `DownloadViewModel` 切到
    「实例版本」并进入整合包模式；详情弹窗在导航开始时立即关闭（关闭弹窗不取消部署）。
  - 本地导入（实例页「导入整合包」与全局拖拽）解析成功后经 Shell 进入同一个整合包模式；
    主动选择与拖拽共用 `InstancesViewModel.ImportModpackFileAsync`，不建立第二套部署流程。
- **清单装填与锁定**：在线来源按选定版本重新解析并下载归档（`IContentInstallService.PrepareModpackAsync`，
  归档落在启动器下载目录），随后 `ModpackArchiveInspector.Read` 解析清单；本地来源直接读取归档。
  页面沿用实例版本页布局：整合包版本、归档大小、游戏版本、类型、加载器与加载器版本整体灰化只读，
  实例名称自动填充为整合包名称（可改），安装位置沿用既有规则。缺失 Minecraft 版本、缺失加载器版本、
  Quilt、未知加载器、Cleanroom 非 1.12.2 都显示准确原因且不允许部署；页面不补任何清单未声明的组件。
- **模组数量与预览**（`download-modpacks.md` §8.1）：`ModpackArchiveInspector` 新增
  `ModFiles`（Modrinth 只统计 `mods/` 下非客户端不支持的文件；CurseForge 统计 required 条目），
  清单没有文件列表时数量显示「未知」而不是 0。选项不可编辑但保持正常视觉样式，点击打开只读预览弹窗
  （`ModpackModListDialogViewModel`）；CurseForge 清单不含文件名，预览显示项目/文件 ID，不猜文件名。
  部署期间显示「正在安装 已完成/总数」，完成计数来自领域真实下载计数，成功后才切换为「已安装」。
- **统一部署**：`IContentInstallService.DeployModpackAsync` 在同一个 Import 任务中创建新实例并导入，
  导入失败删除本次新建实例（无法删除时失败详情写明）；成功后页面再用
  `IVersionInstallService.InstallAsync` 安装游戏文件与加载器。安装失败保留实例，重试只重跑安装；
  导入失败重试从头部署。任务中心只有一条当前任务，不建立第二套任务列表。
- **产物大小**（`download-instance-versions.md` §9）：新增 `IVersionDownloadSizeEstimator`：
  - Minecraft 游戏文件 = 官方版本 JSON 的 `downloads.client.size` + 当前平台实际选择的
    library/natives 大小 + `assetIndex.totalSize` + `logging.client.file.size`；缺少任一项时整体
    「大小未知」，不显示 0；版本 JSON 地址来自版本清单（`MinecraftGameVersion.Url`），
    失败时退回本机版本文件。该数字是产物总大小，可能包含本机已有、安装时会被哈希跳过的文件。
  - 整合包归档大小来自来源声明的字节数或实际归档文件长度。
  - Fabric 加载器仍保持「大小未知」：Fabric Meta 的 profile 不声明 fabric-loader 自身 jar 的大小，
    只汇总其余库会漏项，因此不显示不完整数字；Forge/NeoForge/Cleanroom/OptiFine 目录同样没有
    声明安装包/库大小。模组数量与归档大小是真实值。
- **进度百分比**：安装阶段同时有可靠字节总量时按字节计算百分比（避免单个大客户端文件下载中一直显示
  0%），否则退回文件计数百分比；总进度仍只用线性进度条与阶段文本。
- **重名与自身实例**：部署刚创建的实例不参与后续同名校验（按稳定 InstanceId 排除），
  避免成功或安装中页面把本次创建的实例显示成“实例已存在”。
- 测试：核心 `ModpackDeploymentTests`（7：清单模组计数与只读 ID、缺失文件列表为未知、按版本准备、
  部署创建+导入+回滚、归档缺失拒绝）与 `VersionDownloadSizeEstimatorTests`（5：客户端/库/资源/日志求和、
  资源总大小缺失为未知、日志大小缺失为未知、Fabric profile 求和与缺失大小保持未知）；
  应用新增 16 项（整合包锁定/名称装填、部署顺序、安装失败只重试安装、准备失败不创建实例、
  本地归档同模式、Quilt 拒绝、成功后必须重新布置、重新布置退出模式、预览列表、退出模式清名、
  真实大小、大小未知不阻塞、整合包大小来自清单、非 Fabric 大小未知、自身实例不算重名、字节百分比）。

### 2.9 下载中心数据包世界目标（B4a 收尾）

页面规格：`download-resource-template.md` §5.3、§8.3；领域边界：ADR 0026/0027/0047。

- **领域（新增）**：`IDatapackService` / `DatapackService`（`src/ECL.Minecraft/World/DatapackService.cs`）：
  - `DatapackTarget(InstanceId, FolderName)` 是由世界发现结果构造的不透明目标；每次读写前重新扫描该实例的
    `saves` 并验证目标仍属于同一 InstanceId、位于 `saves` 内且不是重解析点，否则拒绝写入。
  - `ListWorldsAsync` 只返回 `level.dat` 可读的世界（不可读的世界以扫描警告说明被排除）；同名世界靠存档目录区分。
  - `ListAsync` 读取世界 `datapacks/*.zip` 与 `level.dat` 的 `DataPacks.Enabled/Disabled`，返回
    未知/缺失/重复/未受管条目；不猜测数据包内容或版本兼容性。
  - `ApplyAsync` 按目标顺序重写受管的 `file/<文件名>.zip` 条目，保留 `vanilla` 与加载器内置条目的原位置；
    旧世界没有 `DataPacks` 时按游戏默认值 `vanilla` 起头；没有变化时不写盘。
  - `ImportAsync` / `DeleteAsync` 经 ADR 0047 的 `InstanceTransactionCoordinator` 写入/删除该世界
    `datapacks`；删除在同一次事务中清理 `level.dat` 引用，`level.dat` 无法解析时拒绝删除。
  - `WorldLevelInfo` 新增 `DataPacksEnabled` / `DataPacksDisabled` / `HasDataPacks`，数据包状态与资源包的
    `options.txt` 完全分离。
- **受控安装**：`IContentInstallService.InstallVersionToWorldAsync` 按来源重新解析版本文件，先下载到启动器
  缓存暂存目录，再经 `IDatapackService.ImportAsync` 事务写入所选世界；整合包与其它内容类型仍走原路径。
  已存在同名文件时返回真实原因（`datapack.target_exists`），页面显示红色失败而不是覆盖。
- **页面**：数据包分类不再是“能力未接入”的阻断页：
  - 第一行英文搜索与来源切换保持不变；第二行固定要求实例与具体世界，未选世界时不提交搜索。
  - 世界选择器只列出该实例的真实世界（显示世界名 + 存档目录名），切换实例会清空并重新加载世界；
    扫描警告（例如被排除的不可读世界）在页面下方以弱提示显示。
  - 目标确认弹窗同时写出实例名与世界名；「安装到其他位置」打开 `DatapackTargetDialogViewModel`：
    先选已登记实例，再从该实例的世界清单选具体世界，不提供任意目录。
  - 行内进度沿用统一状态线；成功文案为“已安装到实例「…」的世界「…」”。数据包安装**不会**自动启用数据包，
    启用状态与顺序只由世界级配置决定。
- **无障碍**：`ContentTargetInstanceRow.ToString()` 改为实例名称（此前 UIA 暴露记录类型的默认字符串）；
  世界条目只暴露世界名与存档目录名。
- 测试：核心 `DatapackServiceTests`（9：列表与启用状态、顺序重写与内置条目保留、旧世界默认 vanilla、
  导入/删除与引用清理、重复导入拒绝、缺失/重复/未受管报告、跨实例/目标消失拒绝、level.dat 不可读拒写、
  损坏压缩包只标记损坏）+ `ContentInstallTests`（2：按世界安装并落盘、目标实例不匹配拒绝）；
  应用 `ContentDiscoveryPageTests` 新增 4 项（世界选择门槛、实例+世界确认与安装目标、更换目标走世界选择、
  世界选择弹窗加载与返回目标），并移除旧的“阻断状态”断言。

### 2.10 实例工作区数据包页（B2 收尾）

页面规格：`resource-library-template.md` §5/§5.1；领域边界：ADR 0026/0027/0047。
页面对象 `InstanceDataPacksViewModel` + 视图 `InstanceDataPacksView`（`App.xaml` DataTemplate），
由 `InstanceDetailPageViewModel` 持有并在关闭标签时释放；导航项 `content.dataPacks` 在页面对象存在时可用。
所有世界解析、`level.dat` 读写与文件操作只经 `IDatapackService`，页面不拼接世界路径、不写 NBT。

- **世界上下文（§5.1）**：进入页面先读取该实例的真实世界清单（`ListWorldsAsync`，只含 `level.dat` 可读的世界），
  未选择世界时不读取数据包、不提交搜索式的任何写入；世界条目显示世界名与「存档目录」，
  同名世界靠存档目录区分。世界下拉为**单向绑定**：`SelectionChanged` 经 `TrySelectWorld` 决定是否接受，
  存在待应用更改时必须确认丢弃；拒绝时把下拉恢复到当前世界，保证控件显示与实际写入目标一致。
  切换世界立即清空旧工作副本并重新读取新世界，不把旧世界的启用/顺序待应用更改带入。
- **双表与计数**：已启用 / 未启用两张表格，各自在表格内部左下角显示「共 x 个」；行字段为状态、
  名称及简介、版本范围、功能。名称取文件名，简介只显示 `pack.mcmeta` 的真实 `description`（缺失不占位）。
  版本范围只显示包内 `supported_formats` / `pack_format`（「格式 N」），
  **不套用资源包格式对照表**（两者编号体系不同），缺失显示「未知」。
- **状态**：绿色=正常/已启用、红色=压缩包损坏、灰色=无 `pack.mcmeta`（未识别）；
  数据包没有“游戏记录为不兼容”的世界级数据源，因此不产生黄色异常态。
- **拖动与启用**：同表拖动调整加载顺序，跨表拖动启用/禁用；拖动只改工作副本，不写盘。
  每行「启用/禁用」按钮是等价的键盘可达操作。
- **应用**：位于已启用表格右上、表格外，无更改灰色、有待应用更改绿色；调用
  `IDatapackService.ApplyAsync` 一次写入该世界 `level.dat` 的 `DataPacks/Enabled`（保留 `vanilla` 与
  加载器条目的相对位置），失败时保留待应用更改。未启用顺序只在本页整理，不写入世界配置。
- **添加 / 删除**：添加走文件选择器（`*.zip`）与 `IDatapackService.ImportAsync`，不自动启用；
  删除确认框同时写明世界名与文件名，领域在同一次事务中删除文件并清理 `level.dat` 引用。
- **下载数据包**：`DataPackPageFactory.OpenDatapackBrowser` → `ShellViewModel` →
  `DownloadViewModel.OpenContentBrowserAsync(ContentKind.DataPack)`，切到下载中心「数据包」页签并带入实例；
  下载页自身仍要求再选具体世界（§2.9），本页不伪造世界目标。
- **表格宽度**：窄窗口（1080×720）下两张表格在表格内部横向滚动（最小可读宽度 520），
  世界选择器与提示上下排列，页面只在自身滚动容器内滚动，不横向溢出。
- 测试：`tests/ECL.App.Tests/Workspace/DataPacksPageTests.cs`（16 项：世界门槛与空状态、双表独立计数、
  拖动/按钮的待应用状态、应用目标与顺序、切换世界确认/拒绝/重新加载、格式编号不套用资源包表、
  删除确认、应用失败保留更改、刷新确认、缺失/未受管/重复警告、无世界/世界读取失败、添加与下载入口、
  释放后停止扫描）；`InstanceWorkspaceNavigationTests` 新增「提供页面后数据包项可用」。
- 实机（1500×950、1080×720，zh-CN/en-US）：临时实例（`instance create` 新建、验收后删除）上
  真实选择世界、禁用/启用 → 应用写入 `level.dat`（`Enabled` 由
  `[vanilla, file/enabled-one.zip, file/enabled-two.zip]` 变为 `[vanilla, file/enabled-one.zip]`）；
  删除损坏包同时移除文件且不误删其它条目；取消切换世界保持原世界与待应用更改、确认后加载另一世界且
  两个世界的 `level.dat` 均未被越界写入；「下载数据包」切到下载中心数据包页签并带入实例目标。

### 2.11 设置中心与常规页（B3 步骤 1）

页面规格：`settings.md` §3/§4、`settings-general.md` §3–§8；配置契约：ADR 0002。
设置中心改为与实例工作区一致的纵向二级导航：8 个固定分类（常规 / 界面偏好 / Java 管理 /
游戏与启动 / 下载 / 网络 / 存储与路径 / 扩展管理），首次进入与重新进入都以“常规”为默认落点，
页面内切换分类保留当前会话选择。窄窗口（<640）下导航折叠为顶部分类选择器。

- **页面目录只做呈现映射**：现有元数据以 `CategoryKey` 表达区块，不是二级页；
  `SettingsPageCatalog` 用静态表决定“哪个键显示在哪一页/哪一组”，不复制默认值、校验或持久化语义。
  设置项仍全部来自 `ISettingsService.GetDefinitions`，zh-CN/en-US 文案走 Loc Key。
- **本轮可用页面**：常规、界面偏好、Java 管理（迁移中的过渡页：只保留仍在使用的
  `java.defaultPath` 与 `java.runtimeProvider`，运行时清单/优先级/验证/卸载列在能力缺口）、
  游戏与启动、下载、网络；**存储与路径已由 §2.16 完成**，扩展管理保留导航项与准确的不可用原因，
  不构造空页。Java 一级导航暂时保留，直到设置内的完整 Java 管理页通过验收。
  **该过渡页与一级导航已在 §2.12（B3 步骤 3）完成迁移并移除。**
- **保存策略**（`settings.md` §4.1）：Boolean/Select 在值变化后即时校验并保存；
  Text/Number/Path 提供显式「应用/还原」，非法值不写入配置并在该项下显示真实原因。
  每项显示当前值、默认值、来源与“需要重启”标记。
- **Select 别名**：D-01 的 `stable/beta` 旧值按 `main/dev` 的同一展示项解析（显示 Stable/Beta），
  只有用户重新选择后才写入规范值；不新增配置键、不改默认值。
- **恢复默认值**：只清除当前分类的设置项，执行前经 Dialog Engine 确认；
  不删除 Java、实例或文件，不影响其它分类。取消时不产生任何写入。
- **设置搜索**：搜索已实现页面的设置名称与说明，结果显示所属分类并可定位。
  目标项被 `VisibleWhen` 隐藏时，定位到控制它的选项并说明解锁条件，不报告“已定位”指向看不见的行。
- **代码签名信任卡**：常规页展示 ECL Authenticode 证书的真实状态、Subject、SHA-256 指纹与有效期；
  导入/撤销走 `CodeSigningTrustService` 与确认弹窗，拒绝或 UAC 取消不影响 ECL 与更新 Manifest 验签。
  读取失败时只显示真实原因，不显示空指纹或可执行的假按钮。
- **能力缺口如实呈现**：规格要求但当前版本没有底层能力的项（Windows 登录启动、系统托盘与关闭行为、
  更新周期/自动下载、通知、普通操作确认、字体/缩放/动效、内存自动策略/全屏、下载重试/限速、
  新安装代理默认值迁移）集中在「尚未接入的能力」分组说明原因，不放置不生效的控件。
  **网络结构化端点/凭据/测试连接已由 §2.15 完成；D-02 仍受安装代际信号阻断。**
- 设置中心不再提供“全局/实例”作用域切换，也不再编辑 `instance.*`（实例专属配置归实例工作区，C-13）。
- 测试：`tests/ECL.App.Tests/Settings/SettingsPageTests.cs`（14 项：分类顺序与默认页、不可用分类不可选、
  会话保留与重新进入、全局元数据与无实例作用域、D-01 别名显示/写回、即时保存与非法输入拒绝、
  `VisibleWhen` 联动、按分类恢复默认值与取消、名称/说明搜索与定位、隐藏目标的解锁说明、
  能力缺口分组、Java 过渡页保留现有选项、代码签名卡归属）。

**B3 尚未完成的子步**（不冒充已实现）：General 的启动/窗口/通知/更新周期能力（需要启动注册、
系统托盘、通知渠道与更新调度接线）、扩展管理的权限执行与生命周期。
界面偏好的字体/字号/缩放/动效、游戏与启动的内存自动策略与全屏、下载的自动重试/限速、
网络的结构化代理端点与受保护凭据（D-04）已完成；新安装代理默认值迁移（D-02）仍需可靠的安装代际信号。
Java 管理的运行时清单/优先级/验证/卸载已由 §2.12 完成。
存储与路径（工作区迁移、路径总览与存储中心入口）已由 §2.16 完成；ECL 数据目录、缓存、
日志与 Java 运行时目录仍只读显示，不提供迁移入口。

### 2.12 Java 管理页（B3 步骤 3）

页面规格：`java-management.md` §3–§8；ADR 0002（配置）、ADR 0014（运行时优先级与生命周期）、ADR 0050（设计系统）。
设置中心「Java 管理」从过渡页替换为真实页面，同时移除一级导航的 Java 入口（旧 `java` 深链接与
`ECL_START_PAGE=java` 经 `NavigationAliases` 重定向到设置）。

- **清单来源与页面对象**：`SettingsJavaPageViewModel` 实现 `ISettingsPage`，视图为
  `Views/SettingsJavaPageView.xaml`（`App.xaml` DataTemplate 注册）；构造函数不再让设置中心依赖
  `IInstanceService`，页面由 DI 单例持有，切换分类再返回不重复扫描。
- **懒发现**：构造设置中心、打开 ECL 或默认导航都不扫描 Java；首次进入 Java 页才执行一次有界发现
  （`IJavaLocator.RefreshAsync`，仍使用 JAVA_HOME/PATH/已知系统目录/搜索路径的有界范围），
  同一运行周期内再次进入复用结果，只有「重新扫描」显式重扫。
- **紧凑单行表格**：列顺序固定为拖动标志 / 主版本 / 名称 / 路径 / 操作，状态以颜色 + 文字在行内呈现
  （可用、验证中、异常、信息未知、32 位不支持），不新增列；名称优先使用真实验证输出（发行版 + 完整版本），
  未验证时回退 release 元数据并保持未验证状态，不把目录名推断当结论。
- **领域能力（`src/ECL.Java/`）**：
  - `JavaRuntimeRegistry`（`config/java-runtimes.json`）：用户登记的导入项与「从 ECL 列表移除」的规范化可执行文件路径，
    原子写入、损坏时按空状态处理；扫描遗漏不会删除登记，已移除项不会被扫描自动带回。
  - `ProcessJavaVersionProbe` + `JavaRuntimeValidator`：执行 `java -XshowSettings:properties -version`（有界超时、可取消），
    从真实输出解析版本/发行版/架构；无法启动、超时、非零退出码、输出不可读分别给出结果，32 位单独标记。
  - `JavaRuntimeCatalog`：发现 + 登记合并去重（同一规范化可执行文件路径只出现一次），排除已移除项，
    用户优先级在前、其余按发现顺序；`Runtimes` 目录的直接子目录标记为 ECL 托管。
  - `JavaRuntimeUninstallService` + `IJavaRuntimeUsageProvider`：所有权校验（必须是托管目录的直接子目录、
    不是链接、可执行文件在目录内）、实例固定引用与活动游戏会话探测；删除前再次复核并如实返回失败原因。
- **优先级与持久化**：新增隐藏键 `java.priority`（有序可执行文件路径，分号分隔），只有用户实际拖动排序后才写入；
  未配置时沿用 `java.defaultPath` 的旧顺序，升级前的候选顺序不变。`JavaSelectionService`、CLI `java list`
  与页面共用 `JavaPriorityOrdering.SelectSeed`；排序只改变候选偏好，兼容性判断仍由启动选择流程负责。
- **移除与卸载语义**：移除只写登记状态并调用确认弹窗，绝不删除文件，也不改写实例 `instance.java.executablePath`；
  外部 Java 的卸载按钮禁用并说明原因；托管运行时的卸载在确认弹窗中显示名称、版本与完整路径，
  失败或部分完成时保留该行、重新验证并显示真实原因，不伪装成功。
- **添加与验证**：添加通过文件选择器执行真实验证，32 位直接拒绝并说明原因，重复路径提示已存在；
  外部 Java 只登记引用（不复制/移动/接管）；「验证所选」只重新验证选中行，一项失败不影响其他候选。
- **窄窗口与搜索**：1080×720 下设置分类折叠为顶部选择器，表格与来源分组保持最小可读宽度并在内部横向滚动，
  页面只纵向滚动、不横向溢出；设置搜索可定位 `java.defaultPath` / `java.runtimeProvider`。
- 测试：`tests/ECL.Tests/Java/JavaRuntimeManagementTests.cs`（9 项：登记/移除持久化、损坏状态、
  版本输出解析、托管目录边界、托管卸载、外部拒绝、引用阻断与探测失败、清单合并/顺序/来源）；
  `tests/ECL.App.Tests/Settings/JavaManagementPageTests.cs`（14 项：首次扫描与复用、行字段、
  优先级与旧配置兼容、仅排序写优先级、移除语义、外部不可卸载、托管卸载与失败、阻断原因、添加拒绝与登记、
  重复提示、验证所选、扫描失败空状态、释放后停止、搜索定位）；
  `SettingsPageTests` 断言设置中心不再在构造时扫描 Java，且 Java 页不是元数据分组页。

### 2.13 界面偏好页（B3 步骤 2）

页面规格：`settings-interface-preferences.md` §2–§10；ADR 0002（元数据配置）、ADR 0021（动效）、
ADR 0050 §2/§5/§6（字体、排版与 DPI）。设置中心「界面偏好」从“语言 + 能力缺口”替换为真实页面，
字体、字号档位、界面缩放、语言与动效偏好全部接入；品牌主题固定，页面不提供主题色、主题包或浅色模式。

- **页面对象**：`SettingsInterfacePageViewModel`（实现 `ISettingsPage`）+ `Views/SettingsInterfacePageView.xaml`
  （`App.xaml` DataTemplate 注册）。四组偏好：字体与文字、界面缩放、语言、动效；另有一组
  「界面偏好的固定边界」如实列出不提供的能力（主题色/浅色模式、逐控件字号与间距、字体导入、
  Windows 显示设置、游戏内语言、逐项动画参数）。
- **新增配置键（ADR 0002，默认与校验都在元数据里）**：
  - `launcher.interfaceFont`：字体族，默认 `harmonyos-sans-sc`；选项表只包含随包且已验证的字体族，
    当前版本只有 HarmonyOS Sans SC（`Fonts/` 下 Regular / Medium / Bold 三个静态字面）。
  - `launcher.fontSize`：字号档位 `standard` / `large` / `xlarge`（1.0 / 1.1 / 1.25）；
    **不提供“较小”**：继续缩小会跌破 ADR 0050 §5 的可读性底线。
  - `launcher.uiScale`：界面缩放 80–150%、步进 5%（`Validate` 同时拒绝非整档值，CLI 也写不进 83%）。
  - `launcher.animationsEnabled`：界面动效开关，默认开启。
  - `launcher.language` 默认值改为 `system`（跟随系统），保留 `zh-CN` / `en-US`；已保存的旧值不迁移。
- **字号档位（`UiTextScaler`）**：页面字号来自设计令牌与局部样式（数百处），因此不在每个 XAML 再引入第二套
  资源键，而是在界面树上按同一倍率换算“元素自己的”字号：只处理有局部值/样式值的元素，纯继承的元素跟随父元素、
  不重复乘算；记录原始字号，倍率变化时从原始值重算，恢复 1.0 时还原；后加载的元素（虚拟化列表、弹窗模板、
  页面切换）通过 `Loaded` 路由事件补做；数据绑定或动画驱动的字号不动。只改表现层字号，不触碰模板、图标尺寸
  与业务状态。
- **界面缩放（`UserInterfacePreferences.ApplyLayoutScale`）**：对窗口内容应用 `LayoutTransform`，
  WPF 按缩放后的可用尺寸重新测量，页面因此走既有的窄窗口响应式逻辑（折叠导航、表格内部横向滚动），
  而不是把内容裁掉。窗口尺寸与 `MinWidth`/`MinHeight` 同步按比例变化，保证任何档位下布局的可用逻辑尺寸
  都不低于设计系统的最小窗口（1080×720）；窗口最终受 `SystemParameters.WorkArea` 限制，被限制时页面
  仍只在自身滚动容器内收缩。用户手动改过窗口尺寸会按当前比例折算回 100% 的等效尺寸；被屏幕限制过的尺寸
  不会被当成新基准（否则“125% → 恢复默认”会把窗口越缩越小，实机复现后修复）。
  Windows 显示缩放、分辨率与系统字体不受影响。
- **语言**：下拉显示“跟随系统 / 简体中文 / English”，带「需要重启」徽标；页面另起一行显示
  **当前实际使用的界面语言**。启动时 `SettingsBootstrap.ResolveLanguage` 把未设置/空/`system` 解析为
  操作系统首选语言，不支持的语言仍由 `JsonLocalizer` 回退到 en-US；CLI 与 GUI 共用同一解析。
  语言不修改 Minecraft 实例语言、资源包或系统语言。
- **动效**：`MotionSettings` 从“只读 Windows 偏好”改为用户偏好 + 系统偏好的组合（`UserEnabled`），
  页面开关写入 `launcher.animationsEnabled`；关闭后页面转场、弹窗淡入与按钮按压时长为零，
  状态提示、进度、焦点与后台任务不受影响；Windows 已要求减少动画时页面显示真实原因且动效保持关闭。
- **字体族生效路径**：主题里的字体声明改为 `DynamicResource UiFontFamily` / `UiMediumFontFamily`，
  `UserInterfacePreferences` 在选择非默认字体族时覆盖应用级资源，默认字体族则移除覆盖；
  当前只有一个随包字体族，因此默认情况下资源保持主题原值，机制已为后续字体扩展就绪。
- **保存失败与恢复默认**：滑块类设置保存失败时回到最后一次有效值并显示真实原因；
  「恢复默认」只清除界面缩放键（默认值以后调整仍然生效），「恢复本分类默认值」只清本页五个键。
- 测试：`tests/ECL.App.Tests/Settings/InterfacePreferencesPageTests.cs`（9 项：四组与随包字体、
  无主题/逐控件字号设置、字体族与字号档位即时保存、缩放步进与恢复默认、保存失败回退、
  语言“跟随系统 + 需重启”与实际语言、动效开关驱动策略、按分类恢复默认、搜索定位与高亮）；
  `tests/ECL.App.Tests/Shell/UserInterfacePreferencesTests.cs`（23 项：档位倍率与未知值回退、
  缩放松紧与边界、字号取整、界面树换算/还原/新增元素/数据绑定跳过、窗口与最小尺寸缩放、
  屏幕限制后的往返恢复、字体资源覆盖与移除）；核心 `SettingsServiceTests` 增加默认值、
  非法缩放/字体/档位拒绝与 `ResolveLanguage` 分支覆盖。

### 2.14 游戏与启动 / 下载页（B3 步骤 4）

页面规格：`settings-game-launch.md` §4–§8、`settings-downloads.md` §3–§7；ADR 0002（元数据配置）、
ADR 0005（下载源）、ADR 0020（下载产物生命周期）。本步把「游戏与启动」从通用元数据页替换为专用页面，
并接通下载重试/限速能力；不新增主题、通知或系统显示设置。

- **游戏与启动页（`SettingsGameLaunchPageViewModel` + `Views/SettingsGameLaunchPageView.xaml`，
  `App.xaml` DataTemplate 注册）**：四个分组（内存分配 / JVM 参数 / 游戏窗口 / 启动行为）+
  「启动行为的固定边界」。内存分配策略与手动值复用元数据行；下方「当前内存状态」只显示
  `IMemoryAllocationService` 的结果（物理总量、当前可用、自动/手动分配值），未知时显示「不可用」，
  不写 0；手动值超过当前可用内存或自动算法读取不到系统内存时显示真实原因警告。
- **内存策略（D-03）**：新增 `java.memoryPolicy`（`auto` 默认 / `manual`，Select 即时保存）；
  `java.defaultMemoryMb` 继续作为手动数值保存，策略为自动时数值保留、行隐藏且不生效。
  `JavaSelectionService`、实例工作区内存条与设置页共用 `MemoryAllocationService`：
  自动算法按**当前可用物理内存**分档（例如可用 7103 MB→3072 MB、16384 MB→8192 MB、
  32768 MB→16384 MB、49152 MB 及以上→24576 MB），为系统与 ECL 保留空间，
  不使用物理内存总量的固定比例；读取不到系统内存时回退 4096 MB 并标记不可靠，启动步骤给出明确警告。
  实例 `instance.java.memoryMb` 始终优先；全局 JVM 参数只在实例无专属参数时生效。
  `MemoryPolicyMigration` 在 App/CLI 启动时执行一次：只有显式保存过 `java.defaultMemoryMb` 时才写入
  `manual`（数值原样保留），否则保持默认 `auto`；策略键已存在时直接返回，可重复执行。
  **旧版本“整页保存”可能把默认 4096 也写进配置**，此时按持久化修改记录迁移为手动 4096（与升级前
  行为一致）；未写过内存键的用户保持自动。该兼容边界记录在交接文档 §6.7。
- **游戏窗口**：宽度与高度作为一组校验并共同提交（`ApplyWindowCommand` 先验证两者，任一失败不写盘；
  高度写入失败时恢复宽度），默认 1280×720；`game.fullscreen`（默认关闭）是独立即时保存的开关，
  开启后仍保留窗口尺寸。`CommandLineBuilder` 在全屏开启时向游戏主类追加 `--fullscreen`，
  不修改 Windows 分辨率/DPI/显示缩放。启动行为分组保留 `game.showConsole` / `game.extraArguments`；
  页面不提供跳过启动检查、忽略错误或启动后 ECL 窗口行为的开关（这些边界以文字说明）。
- **下载页（`SettingsPageCatalog` 映射，仍为通用元数据页）**：分组改为「下载来源 / 并发与限速 /
  重试与失败恢复」。新增 `network.autoRetry`（默认开启）与 `network.maxRetryAttempts`
  （1–10，默认 3，关闭自动重试时隐藏）；`network.speedLimitMbps` 从“仅展示”改为真实控件。
  能力缺口改为「关闭完整性校验 / 自定义镜像地址 / 下载缓存位置 / 下载后自动安装」，
  不再声称重试与限速未接入。
- **下载引擎**：`DownloadService` 按配置解析每个地址的最大尝试次数（自动重试关闭时为 1），
  只重试可恢复的网络/传输错误；大小/哈希校验失败不再重试，断点文件不可用（需丢弃重来）使用
  内部 `DownloadRestartRequiredException` 保留可重试语义。`DownloadRateLimiter` 是批次级共享令牌桶
  （256 KB 突发、按 `network.speedLimitMbps` 折算），在两次读取之间等待；限速时按已知大小延长本次
  尝试超时，避免低速被误判为网络超时。限速不中断正在传输的文件。
- **顺带修复**：`SettingsViewModel.ResetCurrentPage` 改为调用 `ISettingsPage.ResetToDefaults`，
  界面偏好与游戏与启动等专用页的「恢复本分类默认值」不再无效；`OptionChoiceItem.ToString()`
  返回本地化显示名，设置下拉的 UIA/读屏不再暴露 record 默认字符串。
- 测试：核心 +28（`MemoryAllocationTests` 18：自动分档/未知值/真实采样/手动/实例优先/旧配置兼容/
  未知回退/默认用户迁移/显式 4096 与 8192/幂等/损坏配置；`JavaSelectionServiceTests` +1 自动策略与实例优先；
  `LaunchPipelineTests` +2 自动内存命令与 `--fullscreen`；`DownloadServiceTests` +3 关闭重试/
  配置最大次数/限速；`DownloadRateLimiterTests` 4；其余为断言调整）。应用 +10
  （`GameLaunchAndDownloadsPageTests`：四组与真实内存结果、手动切换与保留、旧配置兼容、压力警告、
  系统内存不可用、窗口联合校验/全屏/还原、下载重试与限速控件、搜索定位、专用页恢复默认）+
  `SettingsPageTests` 适配专用页与下拉 `ToString` 断言。
- 实机：1500×950 / 1080×720、zh-CN / en-US；内存策略自动→手动切换、真实可用内存对应自动值、
  窗口非法宽度阻止保存、全屏开关即时写盘、下载自动重试关闭后最大次数隐藏；`ecl launch --dry-run`
  实际命令包含 `-Xmx3072M`（自动）与 `--fullscreen`，仍保留 `--width/--height`。
  窗口尺寸与设置已还原，实例元数据 SHA256 前后一致。

### 2.15 网络页（B3 步骤 5 / D-04）

页面规格：`settings-network.md` §3–§8；ADR 0002（元数据配置）、ADR 0005（下载源）、ADR 0050（设计系统）。
本步把「网络」从通用元数据页替换为专用页面，完成 D-04 的结构化代理端点、受保护凭据、测试连接与
旧 `network.proxyAddress` URI 迁移；**D-02 的新安装默认值迁移仍受可靠安装代际信号工程阻断**，
页面如实说明，不改变默认值、不伪造开关。

- **页面对象**：`SettingsNetworkPageViewModel`（实现 `ISettingsPage`）+
  `Views/SettingsNetworkPageView.xaml(.cs)`（`App.xaml` DataTemplate 注册）；
  `SettingsPageCatalog` 以专用页构造它，不再走通用元数据页。视图中的字段标签显式
  `BasedOn` 主题 `TextBlock`，避免局部样式丢失前景色（实机修复了“用户名/密码”标签呈黑色的对比度问题）。
- **结构化端点**：新增 `network.proxyProtocol`（Select，`http` / `socks5` / `socks4a`）、
  `network.proxyHost`（Text）、`network.proxyPort`（Integer，1–65535、`EmptyIsNull`）。
  旧 `network.proxyAddress` 改为 `IsHidden`，仅作迁移输入。协议集合来自本地代理服务对 .NET
  `WebProxy` 的实测：HTTP（含 Basic 认证）、SOCKS5（含用户名/密码认证）与 SOCKS4a；
  未提供未通过 TLS-to-proxy 端到端验证的 `https` 代理。
- **联合校验与回滚**：协议、主机、端口作为一组由 `ProxyEndpoint.TryCreate` 校验
  （主机非空、不得含协议/路径/空格、端口整数且在范围内）；点击「应用」才写入。
  任一项写入失败时清除本次已写入的键，保留最后一次有效配置；页面显示真实原因与「最后一次有效端点」。
- **受保护凭据（D-04）**：`WindowsDpapiProxyCredentialStore` 使用 DPAPI `CurrentUser` 加密，
  按规范化端点保存到 `config/proxy-credentials.dat`（二进制 blob，不是普通 JSON）；用户名和密码都在
  加密载荷内，不进入 `global.json`、日志、诊断包或可绑定 UI 状态。页面不显示密码；
  `PasswordBox.Password` 只作为参数交给保存方法，保存成功后清空输入；支持「保存凭据」「清除凭据」，
  清除经 Dialog Engine 确认。协议不支持认证（SOCKS4a）时隐藏凭据卡。
- **测试连接**：`ProxyConnectionTester` 与 `SettingsHttpClientProvider` 使用相同的模式/端点/凭据构造，
  对指定 `http`/`https` 目标发送请求；显示到达的 HTTP 状态与耗时、407 认证要求、超时或真实失败原因。
  页面明确说明测试只覆盖一个目标，不代表所有下载源/API 可达。
- **旧地址迁移**：`ProxySettingsMigration` 在 App/CLI 启动时执行（网络页也提供手动「迁移旧地址」）：
  可无歧义解析的 `scheme://host:port` 拆分为结构化键并清除旧值；用户信息迁移到受保护存储，
  不写入普通配置。缺少协议、未知协议（含 `https`）、带路径/查询、非法端口或无法解析时保留原值，
  页面显示修复提示；迁移可重复执行，结构化键写入失败时回滚已写项并保留旧值。
- **下载链路**：`SettingsHttpClientProvider` 仍是唯一代理执行入口。手动配置无效时返回一个
  总是显式失败的 `HttpClient`（错误信息说明去设置 → 网络修正），绝不静默回退系统代理；
  手动代理有效时从受保护存储读取该端点凭据并设置 `WebProxy.Credentials`。
- **D-02 的当前状态**：`network.proxyMode` 默认值仍为 `system`（保留旧安装行为），页面在模式分组下方
  说明“新安装默认不使用代理的迁移仍待可靠的安装代际信号（D-02）”。在信号成立前不得改成
  “数值/键存在即默认 none”，否则会静默改变旧安装的代理行为。
- **测试**：核心 +40 —— `ProxyConfigurationTests`（端点规范化/拒绝、旧 URI 解析与用户信息、
  解析器优先级/不回退、未知模式、迁移成功/幂等/不可解析/部分写失败/凭据保存失败、Windows DPAPI
  往返与密文检查）+ `ProxyExecutionTests`（本地 HTTP 代理 Basic 认证、SOCKS5 认证、none 模式直连、
  无效手动配置显式失败、测试连接 200/407/拒绝连接）；应用 +7
  `NetworkSettingsPageTests`（结构化字段与协议、联合校验、部分写回滚、凭据不会进入设置文本、
  测试连接使用已应用端点与凭据、旧地址迁移/修复提示、恢复默认不清凭据）。
  另更新 `SettingsPageTests` 的搜索定位与专用页断言。
- **实机**：1400×880 / 1080×720、zh-CN / en-US；真实 DPAPI 凭据保存/重启后读取/清除；
  手动端点 127.0.0.1:18081 应用、非法主机阻止保存并保留最后有效端点；
  通过本地代理服务完成测试连接（HTTP 200）；启动迁移把 `http://alice:secret@127.0.0.1:18082`
  拆分为结构化键并清除旧值、凭据从加密存储恢复；不可解析旧值保留并显示修复提示。
  **未使用 ECL 下载/安装流程**，未创建/删除实例，共享 `versions/`、`libraries/`、`assets/`
  在验证窗口内 0 个文件变更；验证后 `global.json` 与实例元数据 SHA256 已还原/一致，
  `proxy-credentials.dat` 已删除。

### 2.16 存储与路径页（B3 步骤 6 / 工作区迁移）

页面规格：`settings-storage-paths.md` §2–§10；ADR 0018（工作区存储与迁移）、ADR 0020
（下载产物生命周期）、ADR 0022（存储分析/安全清理）、ADR 0050（设计系统）。
本步把「存储与路径」从不可用分类替换为专用页面：只读路径总览 + Minecraft 工作区的正式迁移流程 +
存储中心入口。**页面不清理任何文件、不提供伪编辑控件**；ECL 数据目录、下载缓存/产物、日志与
Java 运行时目录当前架构没有独立迁移能力，因此只读显示并在「固定边界」中说明原因。

- **页面对象与接线**：`SettingsStoragePageViewModel`（实现 `ISettingsPage`）+ `SettingsStoragePathRowViewModel` +
  `Views/SettingsStoragePageView.xaml(.cs)`（`App.xaml` DataTemplate 注册）。`SettingsPageCatalog`
  以专用页构造它（依赖 `IAppPaths` / `IWorkspaceService` / `IWorkspaceMigrationService` /
  `IOperationStore`），`SettingsViewModel` 增加可选的同组依赖参数与 `StoragePage` 属性；
  `CanResetDefaults=false`（路径不是元数据默认值，恢复默认不会移动文件）。
- **路径总览**：按真实用途列出工作区、ECL 数据目录、下载缓存、下载产物、日志与诊断、
  Java 运行时、程序文件共 7 行；每行显示完整路径、用途、真实状态（工作区含可用空间/缺失/不可写/无效，
  其余显示目录是否存在）与「打开所在位置」（目录不存在时禁用）。打开失败显示真实原因。
  只读取路径与一次目录探测，**不做体积测量**（分类切换不触发有存储成本的后台任务）。
- **工作区状态**：未配置工作区时显示“尚未设置”与说明并隐藏迁移入口；主工作区为外部登记目录时
  显示“只登记引用、不由 ECL 迁移”，不提供会移动外部实例的入口；缺失/不可写/无效按领域校验结果展示。
- **迁移流程**（唯一入口，调用 `IWorkspaceMigrationService`）：目标位置文本 + 原生文件夹选择器 →
  「检查目标」调用 `PlanAsync` → 展示迁移计划（源、目标、实例数、文件数、数据体积、目标可用空间、
  逐条阻塞项与警告、源目录保留说明）→ 「开始迁移」经 Dialog Engine 确认（目标、实例/文件数、体积）→
  `StartAsync` 执行复制→校验→切换，页面只转发领域真实阶段与文件计数（`正在复制文件：x / y`、
  `正在校验复制结果：x / y`、`正在切换工作区`），不自行推算进度百分比。
- **失败与恢复**：分两个独立入口，不混淆：
  - 可继续状态（`PendingValidation/Validating/ReadyToCopy/Copying/Verifying/Switching/Interrupted`）→
    「继续迁移」（`ResumeAsync`，会重新复制并校验）与「放弃迁移」（`DiscardAsync`，只清理 staging 与记录）；
  - `RollbackRequired`（文件已复制、切换工作区配置失败）→ 只提供「重新切换工作区」
    （新增 `CompleteSwitchAsync`，只重试 `InitializeAsync`，不重新复制）与「放弃迁移」；
  - `Failed` → 只提供「放弃迁移」；已结束（`Completed/Discarded`）不显示恢复卡。
  存在可继续/可切换的未完成记录时，页面阻止生成新计划（必须先处理恢复卡）。
  失败结果使用本次迁移的真实阶段 + 领域错误码映射，**不得把上一次“已完成”记录当成本次失败阶段**
  （实机复现并修复）。
- **活动任务阻断**：`IOperationStore` 中存在未结束任务（Pending/Running/Retrying）时，页面显示
  真实任务标题并禁止检查/开始/继续/切换；生成计划后再次检查，避免复制到一半的工作区。
  取消确认不产生任何写入；没有计划时开始按钮不可用。
- **领域补充（最小、语义不变）**
  - `WorkspaceMigrationPlan.ProblemDetails`：`WorkspaceMigrationProblem(Code, Message)` + `Arguments`，
    与 `Problems`（原始文案，CLI/日志仍使用）逐项对应；页面按稳定代码本地化，
    未识别代码回退领域文案。判据仍只在领域服务里。
  - `IWorkspaceMigrationService.StartAsync/ResumeAsync` 增加可选 `IProgress<WorkspaceMigrationRecord>`：
    复制/校验/切换与文件计数来自 `Update()` 的真实记录；回调异常只记日志，不影响迁移。
  - 新增 `CompleteSwitchAsync()`：只处理 `RollbackRequired`，重试切换后把记录置为 `Completed`；
    其它状态返回 `workspace.migration.not_switchable`/`none`/`target_missing`，不复制、不删除。
- **存储管理入口**：由 Shell 注入回调，`管理存储空间` 进入既有存储中心（不指定实例）；
  设置页不重复实现清理、预览或删除（`settings.md` §5 边界、ADR 0022）。
- **Shell 页脚路径刷新**：工作区迁移在运行期改变主工作区；`ShellViewModel` 订阅
  `IWorkspaceService.Changed` 并刷新页脚的 `GameDirectory`。注意该事件在
  `WorkspaceService.InitializeAsync` 中先于 `IAppPaths.SetGameRoot` 触发，因此读取
  `workspace.Primary?.Path` 而不是当时的 `AppPaths.GameRoot`（实机发现并修复，否则页脚停在旧路径）。
- **本地化**：新增约 120 个 `settings.page.storage.*` 键（zh/en 成对），删除死键
  `settings.nav.unavailable.storage`；迁移警告沿用既有 `workspace.warning.*` 键。
- **测试**：核心 +4 —— `WorkspaceMigrationServiceTests`
  （结构化问题代码与 `Problems` 对齐、真实阶段/文件计数进度、`RollbackRequired` 只切不复制、
  其它状态拒绝切换）；应用 +18 —— `StoragePathsPageTests` 17 项（路径总览与唯一可迁移项、未配置工作区、
  真实计划影响与占用目标阻塞、目标变化使计划失效、空目标拒绝、确认后迁移并保留源、取消零写入、
  计划后条件变化报告失败且不复用旧阶段、完成迁移后失败阶段不复用“已完成”、中断恢复、
  放弃只清 staging、`RollbackRequired` 只切换、失败记录只能放弃、活动任务阻断、存储中心接线、
  搜索定位与行高亮、打开位置的注入与失败提示）+ `SettingsPageTests` 新增存储专用页断言，
  未实现分类只剩扩展管理。
- **实机（2026-11-14，见交接文档 §4.2）**：1400×880 / 1080×720、zh-CN / en-US；
  真实临时工作区（`ECL_DATA_DIR` 隔离）完成小工作区迁移（4 文件）、6004 文件迁移的真实阶段捕获
  （`正在校验复制结果：6004 / 6004`）、中断记录恢复卡 → 继续迁移完成、计划后目标被占用的失败结果、
  「管理存储空间」跳转与页脚路径刷新；源目录始终保留，`workspace.json` 主工作区与
  `workspace-migration.json` 状态一致；验证后临时数据目录已删除，未触碰真实用户配置。
  **未实机覆盖（自动化覆盖）**：`RollbackRequired` 真实切换失败、放弃迁移、外部工作区主目录
  （当前 API 无法构造外部主工作区）、文件选择器成功路径、多 DPI/缩放组合。
- **能力边界**：ECL 数据目录/缓存/下载产物/日志不是可迁移项（由便携模式、`ECL_DATA_DIR` 或安装位置决定）；
  下载缓存与临时产物是同一数据目录下的固定路径，不拆成两个虚构设置；本页不提供清理；
  「多安装位置/新建实例到外部目录」仍是既有领域缺口（ADR 0018/0046），不在本页提供假选择器。

### 2.17 扩展管理页（B3 步骤 7）

页面规格：`settings-additions.md` §3–§9；ADR 0032（Additions 生命周期与 Core API）、
ADR 0045（实例边界）、ADR 0050（设计系统）。本步把「扩展管理」从导航不可用分类替换为专用页面：
已安装扩展的真实状态、官方目录的可安装/更新条目、两阶段安装计划，以及**如实说明的权限与隔离缺口**。
`docs/additions` 注册目录（ADR 0054）与逐项能力授权/运行时强制（ADR 0053）均未实现，
因此页面不显示权限清单、不提供「完全信任」开关、不宣称沙箱隔离。

- **页面对象与接线**：`SettingsAdditionsPageViewModel`（实现 `ISettingsPage`）+
  `SettingsAdditionsRows.cs`（行/计划/筛选的只读投影）+ `Views/SettingsAdditionsPageView.xaml(.cs)`
  （`App.xaml` DataTemplate 注册）。`SettingsPageCatalog` 在拿到 `IAdditionsService` 时以专用页构造它，
  `SettingsViewModel` 增加可选的 `IAdditionsService` 参数；`CanResetDefaults=false`
  （扩展列表与安装记录不是元数据默认值）。构造设置中心不读扩展状态、不访问网络。
- **领域入口 `IAdditionsService` / `AdditionsService`**（`src/ECL.App/Services/AdditionsService.cs`）：
  把已安装状态、目录获取、安装计划、启停、移除与回滚收敛到一处；页面不读写 `additions/`、
  不解析目录 JSON、不构造包地址。目录只来自 `ECL_ADDITIONS_CATALOG`（HTTPS 绝对地址），
  签名公钥来自随包 `additions-trust.json`，HTTP 客户端经 `IHttpClientProvider`（代理设置生效）。
- **已安装列表（真实记录）**：读取 `AdditionStateStore`；名称与用途只在官方目录提供条目时才有来源，
  没有目录时只显示扩展 ID。状态只用能准确判断的部分：已启用/已禁用（安装记录）+
  依赖缺失（安装记录之间的必需依赖）/ Core API 不兼容（记录里的 `coreApiMin/Max` 与
  `AdditionCoreApi.Version` 比较）。状态文件损坏时整页只读并显示真实原因。
- **操作边界**：禁用永远可用且不删除文件或用户数据；启用前检查必需依赖与 Core API 范围，
  不通过时按钮禁用并在 ToolTip/状态行给出真实原因（不写任何状态）；仍有其它已安装扩展依赖它时
  移除被阻止；移除/回滚经 Dialog Engine 确认（移除说明扩展数据目录保留，回滚说明替换版本目录）；
  回滚只在记录的 `PreviousVersion` 存在时提供。
- **官方目录**：只在用户点「检查目录更新」时读取（分类切换与启动不发起网络请求）。
  未配置、URL 非 HTTPS、缺少公钥、签名失败、无可用缓存分别显示稳定错误码对应的本地化原因，
  **不降级为“空目录”**；缓存命中时明确标注可能不是最新状态。可安装列表排除已安装与已撤回条目。
- **安装计划（两阶段）**：`CreatePlanAsync` 由 `AdditionPlanner` 生成计划（依赖、Core API、
  平台与冲突检查都在领域层）；页面展示条目、版本、包大小、必需依赖与 Core API 范围，
  再经 Dialog Engine 确认后 `ApplyPlanAsync` 按计划顺序安装。计划为空时提示“已安装且无需更新”。
- **布局（实机修正后）**：已安装表固定五列压缩为四列——状态 / 名称(含扩展 ID 与一行说明) /
  版本(含类型与安装日期) / 操作；每行固定高度，说明行最多两行且**完整显示**依赖缺失与 Core API
  不兼容原因（短句 + 换行，不靠省略号）；操作按钮在 92px 的两行两列 WrapPanel 中，
  1080×720 且 Inspector 打开时表格仍完整可见（不再需要横向滚动才能点到按钮）。
  列表高度随内容展开，由设置页自身滚动，不再用固定内层高度裁切行。
- **筛选与搜索**：名称/ID/说明搜索 + 只列可准确判断状态的筛选（全部/已启用/已禁用/依赖缺失/
  Core API 不兼容/可更新；“可更新”只在目录给出不同版本时命中）。筛选只作用于已安装表；
  有筛选时计数显示“已安装 N 个 · 当前显示 M 个”，避免与可见行数矛盾。
- **本地化**：新增约 110 个 `settings.page.additions.*` 键（zh/en 成对）；删除死键
  `settings.nav.unavailable.additions`。
- **清理重复入口**：工具箱移除「扩展」页签与 `AdditionsViewModel`/`AdditionRowViewModel`，
  扩展的安装/启停/移除只保留在设置 → 扩展管理（同一组领域服务，能力没有丢失）。
- **测试**：应用 +20 —— `AdditionsPageTests`（进入页面只读本地状态、空状态说明、目录名称回填、
  缺失依赖与 Core API 阻止启用且零写入、禁用/启用的真实调用、依赖阻塞移除、移除与回滚的确认、
  状态文件损坏只读、目录显式读取与可安装列表、目录失败保留真实原因、缓存标注、
  计划预览与二次确认、必须先读目录、无变更提示、可更新条目、搜索与状态筛选、
  领域失败不谎报成功、权限/沙箱边界说明、筛选条目 UIA 名称）+
  `SettingsPageTests` 更新（8 分类全部可用、扩展管理是专用页、缺少领域依赖时保持不可用）。
- **实机（2026-11-14，见交接文档 §4.3）**：`ECL_DATA_DIR` 隔离数据目录中先用真实
  `AdditionInstaller` 安装 5 个扩展（含上一个版本、缺失依赖、Core API 不兼容、数据扩展），
  再由页面读取：真实状态/依赖/兼容结论、禁用→启用写盘、回滚到上一版本、移除并保留数据目录、
  目录“未配置/缺少公钥”两条真实错误、搜索过滤与计数、1400×880 / 1080×720、zh-CN / en-US。
  **未实机覆盖（自动化覆盖）**：可安装列表与安装计划的实际执行（需要真实签名 HTTPS 目录）。

### 2.18 主页与启动卡（B5）

页面规格：`home.md` §2–§6；ADR 0050（设计系统）、ADR 0004（分阶段启动流水线）、ADR 0015（进程日志会话）、
ADR 0039（应用生命周期）、ADR 0041（性能监测）；日志入口契约见 `logs.md` §7.1。
主页从旧的「启动卡 + 运行环境 + 实例列表 + 快速入口 + 最近活动」重排为规格要求的三块，并删除重复入口。

- **页面结构**：① 启动卡片（左：实例信息、启动操作、总启动进度；右：普通/高级模式）；② 中央实例切换栏
  （折叠前四个 + 展开整理 + 编辑/删除模式）；③ 下方三个等高区域（账号切换 / 实例工作区与下载中心入口 /
  Multiplayer）。Java 版本区域、免责声明式小字、快速入口三卡与重复实例列表已按 `home.md` §6 删除。
- **启动卡（左）**：实例名称（3号）、Minecraft 版本 + 加载器 + 存在时的 OptiFine（2号）、实例路径、
  最后游玩时间、"启动游戏"与"预览启动命令"。预览在命令可用时显示等宽命令行；Dry Run 失败时显示真实原因
  （不再静默无反应）。总进度条进行中白色、成功绿色、失败红色并停在失败步骤对应的位置，不显示百分比数字。
- **启动卡（右）**：普通/高级两种模式展示同一次启动。普通模式按 **真实注册的启动流水线** 展示步骤
  （`ILaunchService.Steps`，不硬编码步骤表），每步一条进度条 + 状态文字；失败后的步骤显示"未执行"。
  高级模式显示启动期间 ECL 的轻量实时应用输出（有界 500 行），错误级别按语义色标注；它不取代完整日志阅读器。
- **启动状态按实例持久化**：新增领域存储 `ILaunchStatusStore` / `LaunchStatusStore`
  （`logs/launch-status/<instanceId>.json`）。`LaunchService` 在真实启动开始前写入 Running（立即清除上一次的
  会话与结果），结束时写入成功/失败、失败原因、步骤结果、耗时与本次游戏日志 SessionId；Dry Run 不写入。
  因此 ECL 关闭后再次打开仍能看到该实例上一次启动的结果，直到该实例下一次启动开始。上次尝试没有结束记录时
  显示"未记录结束状态"，不猜测结果；进度与步骤来自记录，不从耗时推断。
- **步骤进度与卡顿提示**：`HomeLaunchPresentation` 用各步骤的名义时长与渐近曲线给出"估量进度"
  （上限 92%，绝不显示为已完成，也不显示百分比）。卡顿提示必须同时满足"步骤运行超过 30 秒"与
  "最近 12 秒没有新的 ECL 输出"两个可观测条件，文案明确"耗时较长不等于失败"，**不**把单纯耗时长判定为卡死。
- **错误日志入口（`home.md` §3.3、`logs.md` §7.1）**：只在最近一次启动失败且存在游戏日志会话时显示
  "查看错误日志"；点击由 `ShellViewModel.OpenLogSession` 打开同一个 Logs Workspace 并传入该次失败的
  **准确 SessionId**。`LogSessionsViewModel.FocusSession` 优先定位目标会话；目标不存在时在页面顶部显示
  "目标会话不可用"，保持没有选中会话，**不静默改看最新会话**。失败但没有会话时显示原因说明，不放不可用按钮。
  新一次启动开始时按钮立即隐藏。
- **中央实例切换栏**：`HomeInstanceSwitcherViewModel` + `HomeInstanceSwitcherViewModel` 的排列存储
  `IHomeLayoutStore`（`config/home-layout.json`，只保存"位置 → 实例 ID"，`null` 表示空位）。
  - 折叠态显示主页默认前四个实例（3号名称、2号加载器信息、右上角加载器名称、底部加载器色条：
    Forge 红 / NeoForge 橙 / Fabric 蓝 / Cleanroom 绿 / 未知灰），展开按钮与实例项等高。
  - 展开态显示全部已识别实例形成的位置布局（空位可见）：拖动整理时拖到已有实例=位置互换、拖到空位=移动过去；
    键盘可用 Ctrl+←/Ctrl+→ 移动当前实例。编辑模式只改排列，不改变实例本身。
  - 删除模式的红色圆形 X 按钮走与实例页完全相同的删除确认流程；删除后原位置保持为空位，其它实例不自动补位；
    新识别到的实例追加到排列末尾，不自动填空位。
  - 本栏不提供实例创建、复制或导入入口（§4.5）；排列只是主页布局偏好，不写入实例记录。
  - 启动进行中禁止切换实例：同一时间只呈现一个实例的启动状态（§3.4）。
- **下方三个等高区域**：
  - 账号：显示当前用户名与类型；皮肤获取尚未接入，按规格使用问号占位头像并说明原因。点击"账号"打开
    账号选择/管理菜单（已登记账户、当前账号、新增离线账户、Microsoft 登录、添加/编辑 Authlib 账户、
    移除账号）。这里仍是全局账户注册表与默认账号的**唯一** UI 入口；新增
    `InstancesViewModel.RemoveAccountAsync`（Dialog Engine 确认 → `IAccountService.RemoveAsync`）。
  - 实例工作区与下载中心：两张卡片上下排列（3号标题、2号简介），作为对应工作区的入口。
  - Multiplayer：只服务 `ecl.multiplayer`。当前没有可验证的正式发布资产 → 显示"尚未发布，暂不可安装"，
    不提供安装操作；已安装的本地版本按"已停用 / Core API 不兼容 / 已安装但没有可打开的功能入口"如实呈现，
    并提供"管理扩展"入口（设置 → 扩展管理），不伪造目标 Workspace。
- **删除重复入口**：实例工作区的全局账号区块已删除（`home.md` §5.1、G-06），账号只在主页维护；
  实例页仍负责实例发现、目录登记、导入与删除。
- **响应式**：内容区宽度小于 700px（1080×720 且侧栏与 Inspector 同时展开）时切换紧凑布局：
  实例切换栏改为两列、下方三个区域纵向排列并保持等高，模式按钮移到列表下方，避免文字被裁切到不可读。
- **本地化**：新增 `home.launch.*` / `home.switcher.*` / `home.account.*` / `home.entry.*` /
  `home.multiplayer.*` / `accounts.remove.*` / `logs.targetUnavailable` 等键（zh/en 成对）；
  删除失效键 `home.news.*`、`home.footer`、`instances.accountTitle`、`instance.detail.accountDesc`、
  `instances.microsoftSignInHint`、`instances.addThirdParty`、`instances.accountSettings`。
- **测试**：核心 +12（`LaunchStatusTests`：状态持久化、按实例隔离、Dry Run 不写入、非法 ID、损坏记录、
  启动服务记录成功/失败、步骤进度事件顺序）；应用 +37（`HomeLaunchCardTests`：无记录/失败/成功/未结束快照、
  实时进度、错误入口生命周期、高级输出与错误级别、预览失败说明；`HomeInstanceSwitcherTests`：四格、空位、
  互换、编辑/删除模式互斥、启动中禁止切换、无创建/复制命令、实例先加载后补齐不产生前置空位；
  `HomeLayoutStoreTests`；`HomeLaunchPresentationTests`；`HomePageStructureTests`：页面区块存在、
  旧区域与实例账号区块不再出现）。
- **实机（2026-11-14，`ECL_DATA_DIR` 隔离数据目录）**：1500×950、1400×880、1080×720（紧凑布局）四个尺寸；
  zh-CN / en-US；展开/编辑/删除模式；通过账号菜单真实新增离线账户并移除（确认弹窗）；
  预置一条失败启动记录后重启验证"状态保留 + 红色失败进度 + 查看错误日志"，
  点击后 Logs Workspace 显示"目标会话不可用"且未切换会话；预览启动命令显示真实命令行。
  真实数据目录未改动：`global.json` SHA256 仍为基线值，实例 `settings.json` 保持 `{"values":{}}`。

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
- `ResourceRowButton`（`Themes/Theme.xaml`）是下载中心内容发现列表的整行按钮样式：卡片外观、
  悬停高亮与键盘焦点环，替代默认 Button 外观，不引入第二套列表控件。

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
- 实例工作区二级导航已在 B2 步骤 2 接入（§2.3）：默认基本信息；除日志外，概览、修改、模组、
  资源包、光影包、数据包、截图、投影、导出均已有页面；日志属 B6，在实现前保持不可用。基本信息顶部 2:1 与 Java
  方块已在 B2 步骤 3a 重写，其余区块仍待后续步骤按 Approved 页面规格完成。
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
- 「修改」页的失败/恢复入口只做了自动化验收：制造一次真实的后写入失败需要可控的网络/文件错误，
  本批实机验证未覆盖，因此「失败且已回滚」「失败且需要恢复」与受控恢复在实机上是 NOT RUN。
- 回滚完整性的口径：`InstallTransaction` 只记录本次新建/带备份覆盖的文件；共享 `libraries`/`assets` 中
  原本存在、被重新下载覆盖且没有备份的文件不会被还原（也不会被报告为回滚失败），因为它们不是数据丢失。
- **已按维护者决策修复（§6.4 决策 a，`ff2b622`）**：`EnsureTextFileAsync` 现在把被它替换或新建的
  `versions/<id>/<id>.json` 与 `assets/indexes/<id>.json` 纳入 `InstallTransaction.TrackFileWithBackup`。
  安装失败时写回原始字节（包括其它启动器写入的合并版本 JSON），成功时按安装结果保留；
  本次新建的基础版本 JSON 在失败回滚时会被删除。诊断文件与回滚信号因此覆盖这两个共享写入。
  「是否允许覆盖不属于 ECL 的基础版本文件」仍沿用决策 a 的选择（允许覆盖，但失败可恢复）；
  如需改为拒绝覆盖，属于后续独立决策，不在本批自行改变。
- Split、Detached、Snapshot 恢复与运行时隔离仍未实现，相关能力不会以按钮或菜单形式出现。
- 资源包与光影包的写入仍由 `IContentPackService` 直接保存 `options.txt`（临时文件 + `.bak` + 原子替换），
  尚未接入 ADR 0047 要求的 `InstanceTransactionCoordinator`；这是与模组/整合包写操作一致的既有实现，
  页面不复制该逻辑，迁移属于后续独立工作项。
- `ResourcePackFormats` 的 `pack_format → Minecraft 版本` 对照只收录已核实的 1.6.1–1.21.4；
  更新版本引入的新格式显示「格式 N」，不按经验外推。对照表随版本发布需要维护。
- 光影支持检测是**能力检测**，不是对单个光影包能否渲染的保证；OptiFine 缺少「构建 × Forge 兼容性」
  的可验证数据源，因此只判定为未知，不冒充支持；`docs/shader-support/` 规则更新时需同步检测实现。
- 截图页只读取 `screenshots/` 的直接子文件；缩略图按需读取整张原图（单张上限 64 MB）后在 UI 线程解码，
  没有持久化缩略图缓存。超大截图或大量截图时首次浏览会有 IO/解码开销。
- 投影页只扫描三个已知工具目录，不提供目录切换；`.bp` 没有公开稳定的可验证签名，因此始终显示
  「格式未验证」；NBT Explorer 与三维查看器依赖尚未实现的 Addition 能力，入口保持不可用。
  支持检测是能力检测，不保证单个投影文件能在游戏内加载或渲染；`docs/schematic-support/` 规则更新时
  需同步检测实现。
- 导出包目前只有 zip（`manifest.json` + `instance/` + `minecraft/`），没有对应的实例包导入流程
  （属 B10），也不包含共享 `libraries`/`assets`/版本目录；完整导出只覆盖实例游戏目录与实例元数据。
  体积是未压缩估算，不预测压缩后大小；导出结束后若文件被跳过，结果会标记为不完整并列出原因。
- 导出页首次打开会遍历实例游戏目录以计算分类与体积，没有增量缓存；大实例首次进入可能需要等待扫描。
  导出任务固定在实例目录 `exports/`，不提供用户选择保存位置（按页面规格避免写入受保护目录）。
- 数据包实例页（`content.dataPacks`）已在 B2 收尾落地（§2.10）：双表、拖动/按钮启停、应用写入该世界
  `level.dat`、受控删除与下载入口；世界目标仍由 `IDatapackService` 在每次写入前重新验证。
  添加数据包依赖系统文件选择器，实机只能覆盖到「取消不写入」；文件选择器/拖拽的成功路径由自动化测试覆盖。
  未启用顺序不写入世界配置；世界被删除/改名/`level.dat` 损坏时的真实时序仍只有领域自动化覆盖。
- 下载中心的实例版本页固定把新实例创建在主工作区：已登记的外部 Minecraft 目录只作为只读来源列出，
  不提供“安装到该目录”的选择。要支持多安装位置需要领域层面的新决策（新建实例目录与身份登记的边界），
  当前如实说明而不是放置会悄悄写到别处的选择器。
- 实例版本的安装项文件大小：Minecraft 游戏文件与整合包归档显示真实总大小（官方版本 JSON / 来源字节数）；
  Fabric API 显示目录字节数；Fabric 加载器、Forge/NeoForge/Cleanroom 加载器与 OptiFine 仍显示「大小未知」，
  因为各目录没有声明安装包或库文件大小，ECL 不用 HEAD 探测或经验估值补数字。
  “游戏文件”是产物总大小，可能包含本机已存在、安装时被哈希跳过的文件；不是本次实际要下载的字节数。
- 下载失败时只提供“重试失败的下载项”，它重跑同一实例的安装并依赖领域哈希校验续传；领域没有
  按单个产物重试的接口，因此不提供逐项重试的假按钮。关闭页面不会取消已开始的任务。
- 整合包模式（在线下载/本地导入共用）已在 B4b 接通；本地导入入口的实机验证只覆盖到解析与路由的
  自动化测试，文件选择器/拖拽没有走人工实机（见交接文档）。整合包清单未声明文件列表时模组数量显示
  「未知」，CurseForge 清单不含文件名时预览只显示项目/文件 ID。
- 整合包模式的加载器大小保持未知（Fabric Meta profile 缺少 fabric-loader jar 大小）；
  清单未声明的附加组件不会由页面补入，安装内容完全由清单与部署规则决定。
- WPF 线程/Dispatcher、共享控件与输出管线测试位于仅 Windows 可运行的 `tests/ECL.App.Tests`；CI 在 Windows job 执行，
  Linux job 不运行该测试项目。
- Java 管理页（§2.12）的排序只有拖动入口：本机键盘/鼠标注入被系统拒绝，实机验证用 UIA 选择/按钮完成，
  拖动排序的实机路径为 NOT RUN，顺序与 `java.priority` 写入由自动化覆盖（与内容页拖动的一致做法）。
  验证状态来自逐运行时执行 `java -version`（有界超时）；页面首次进入会有一次有界扫描 + 逐项验证开销。
  多安装位置、按单个产物重试、ECL 托管运行时的下载与安装记录展示不在本页范围内。
- Java 卸载的所有权边界是「`Runtimes` 的直接子目录」：外部安装、用户导入、旧 `java` 目录或任何链接
  都不可卸载；引用探测覆盖实例 `instance.java.executablePath` 固定引用与仍存活的活动游戏会话，
  探测失败按「无法确认」阻断。部分删除失败时保留该行并重新验证，不宣称已卸载。
- 移除 Java 只解除 ECL 登记/候选关系并写入 `config/java-runtimes.json`，不删除任何文件；
  实例显式固定的 Java 不受影响。已移除的候选不会因后续扫描自动回到列表，重新添加同一路径才恢复。
- 界面偏好页（§2.13）的字号档位用界面树换算实现：它只换算“元素自己的”字号，之后由代码在 `Loaded`
  之后新写入的字号（而不是通过样式/`DynamicResource`）不会被自动重新换算，需要重新应用档位；
  这是避免为 30 个页面逐处引入第二套字号资源键的取舍。当前主题与页面样式都是静态字号声明，未发现该情形。
- 界面缩放的窗口适配以 `SystemParameters.WorkArea` 为上限：在 1680×1050 实机屏幕上 150% 的逻辑高度约 668
  （低于设计最小高度 720），页面改为在自身滚动容器内滚动，仍无裁切或重叠；更高档位在更小屏幕上会被
  工作区限制，页面的可用逻辑宽度仍≥1080。125%/150% 等在**多 DPI 显示器间移动**时的组合行为仍为 NOT RUN
 （与本轮其它批次一致，只有 100% DPI 的实机证据）。
- 动效关闭后的“无动画转场”没有逐帧实机证据：开关写入、`MotionSettings.IsReducedMotion` 与
  `TryBeginPageTransition` 的零时长分支由自动化覆盖，实机只确认了开关状态与页面可用性（NOT RUN：帧级）。
- 界面字体当前只有一个随包且已验证的字体族（HarmonyOS Sans SC）；下拉是真实选项但暂无可切换值，
  新增字体必须先随包提供资源、许可证与字面验证，再登记到 `BundledFontFamilies`。
- 界面缩放的“最小可用档位”是以 ECL 自身最小窗口（1080×720）与当前屏幕工作区验证的：窗口小于该尺寸时
  由 `MinWidth`/`MinHeight` 阻止；若用户把系统 DPI 与 ECL 缩放叠加，可用逻辑尺寸仍按 DPI 归一，
  但 125/150/175% 系统 DPI 下的组合验证仍为 NOT RUN。
- 扩展管理页（§2.17）当前版本不内置官方扩展目录：只有显式配置 `ECL_ADDITIONS_CATALOG`（HTTPS）并随包提供
  `additions-trust.json` 公钥时才会出现“可安装/可更新”条目；未配置或缺少公钥时页面显示真实原因，
  不构造在线目录。目录与签名缓存沿用 ADR 0032 的既有实现；注册目录与 Manifest 治理（ADR 0054）
  尚未实现。
- 扩展的逐项能力授权、授权记录与运行时强制（ADR 0053, Implementation: Not Started）尚未实现：
  页面不显示“声明能力/授予能力/撤销”，不提供「完全信任」开关，也不宣称扩展被沙箱隔离；扩展与
  ECL 同进程加载（`AssemblyLoadContext`），没有操作系统级隔离或资源限制。安装/更新目前只有
  “查看计划 → 确认执行”两阶段，**不是** ADR 0053 的 5 秒阅读缓冲授权弹窗。
- 扩展“可更新”只在目录给出与安装记录不同的版本时成立；没有目录时不会有该状态，也不做本地猜测。
  只有目录签名与包内逐文件 SHA-256 校验，没有独立的发布者身份验证。
- 扩展管理页的目录成功路径（可安装列表与安装计划实际执行）在实机 NOT RUN：它需要真实的签名
  HTTPS 目录，当前版本不提供；该路径由 `AdditionsPageTests` 覆盖。


### 8.1 主页（B5）相关限制

- **启动卡失败路径的实机覆盖范围**：实机验证了"读取并恢复已记录的失败状态 + 查看错误日志入口 + 目标会话不可用"，
  但没有实机制造一次"能产生游戏会话的真实失败启动"（需要完整版本文件、可用 Java、已同意 EULA 与账户，
  再让进程启动失败）。真实失败记录由 `LaunchStatusTests` 与 `HomeLaunchCardTests` 覆盖，实机该子项为 NOT RUN。
- **玩家皮肤**：账户外观 Provider（ADR 0037）对 Microsoft 与离线账户都未声明"读取当前外观"能力，
  因此主页账号区域固定显示问号占位头像与说明，不伪造皮肤；接入需要 Provider 的真实实现与渲染路径。
- **Authlib 账户流程**：主页的新增/编辑 Authlib 账户入口仍复用既有 `AuthlibAccountDialog` 窗口，
  不是 ADR 0052 的 Dialog Engine 内容流程；迁移属 B10（`hints/ui/ecl-pages` 迁移矩阵 #16）。
  Microsoft 登录仍是浏览器 OAuth，不出现用户名/密码表单。
- **Multiplayer**：`ecl.multiplayer` 目前是 Planned，没有可验证的正式发布资产，卡片只显示"尚未发布/暂不可安装"；
  Addition Runtime 未接入，因此即使已安装也没有可打开的功能 Workspace，卡片如实说明并只提供扩展管理入口。
- **卡顿提示的证据边界**：主页只有"步骤耗时 + ECL 输出间隔"两个可观测信号，没有逐步骤的领域心跳。
  步骤天然长时间静默（例如大文件校验）时会显示"耗时较长且没有新输出"的提示，但不会判定为卡死。
- **步骤进度是估量**：普通模式的运行中步骤进度来自名义时长曲线，不是真实测量，也不显示百分比或预计时间；
  只有完成/失败/跳过才是真实结果。历史批次（`f38daa5` 及之前）没有启动状态记录，升级后首次打开显示"还没有启动过"。
- **切换栏整理方式**：只有拖动与 Ctrl+←/Ctrl+→ 两种入口；紧凑布局下模式按钮位于列表下方而不是列表右侧
  （窄窗口的可读性优先）。排列记录为空的实例 ID 会保留为空位，不自动清理。
- **启动互斥**：主页在启动进行中禁用实例切换与再次启动；ECL 仍不支持一个实例的启动进程结束前启动另一个实例（§3.4）。
