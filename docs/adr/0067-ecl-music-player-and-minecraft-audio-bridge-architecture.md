# ADR 0067: ECL Music Player and Minecraft Audio Bridge Architecture

- Decision: Proposed
- Implementation: Not Started
- Date: 2026-11-09
- Scope: ECL Music Player Addition、Core 托管音频播放、Minecraft 游戏内音乐分类控制、Java Agent Audio Bridge、跨 Minecraft 版本与 Mod Loader 兼容性及进程修改知情授权
- Related: [ADR 0032：原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0039: Application Runtime Lifecycle and Graceful Shutdown](0039-application-runtime-lifecycle-and-graceful-shutdown.md)、[ADR 0053：Additions 能力权限模型与用户授权流程](0053-addition-capability-permission-and-user-consent.md)、[ADR 0054：Addition 注册表、清单与生命周期治理](0054-addition-registry-and-manifest-governance.md)、[ADR 0055：ECL 混合工作区与原生工具平台架构](0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)

## 背景

玩家可能希望在 Minecraft 游戏过程中播放自己拥有的本地背景音乐，同时又需要在仔细听取游戏声音时快速暂停外部音乐。若 ECL 音乐与 Minecraft 背景音乐同时播放，可能产生多音源冲突。ECL 需要在不修改 Minecraft 本体、不要求用户手动安装特定 Mod Loader 扩展的前提下，协调 ECL 音乐播放状态与游戏内音乐音量。

该能力还涉及三个不同的运行边界：音乐播放器的用户体验、ECL 音频输出服务，以及运行在目标 Minecraft JVM 内的游戏音量适配器。若将三者全部放入 Addition Runtime，会使音频播放生命周期依赖扩展 UI；若把 Java 代码加入 ECL 主程序，则会不必要地改变 ECL 的技术栈和运行时依赖。

此前对 Minecraft 旧版和新版映射资料的查证发现，不同版本存在可用于设置 MUSIC 音量的游戏内 API，但方法形式、映射名称、类加载行为和 Loader 转换过程并不完全相同。映射/Javadoc 资料只能证明候选 API 的存在，不能证明一个 Java Agent 已经能够在所有版本和 Loader 中稳定运行。跨版本与跨 Loader 能力必须通过真实 JVM 原型验证。

## 决策

### 1. 将音乐播放器登记为独立 Feature Addition

- Addition ID：`ecl.music-player`。
- Addition Name：`ECL Music Player`。
- Addition Type：`Feature`。
- Registry Status：`Proposed`；登记不代表已经实现、发布或可下载。
- Music Player 提供播放器工作区、用户音乐库管理、播放列表和播放控制体验。
- 用户自行提供音乐文件；ECL 不分发音乐曲库，不默认下载或上传用户音乐。
- 音乐原文件应保持原样。索引、播放状态和必要元数据可以由 ECL 管理；卸载 Addition 不得擅自删除用户音乐文件或独立用户资料。
- Music Player 是可选能力；未安装、禁用或不可用时，不得影响普通 Minecraft 启动。

### 2. 由 ECL Core 托管音频播放

ECL Core 提供并管理音频播放服务。Music Player Addition 通过受控 Core API 使用该服务，不在 Addition Runtime 内自行建立与 Core 平行的长期音频播放引擎。

Core 负责音频输出与播放会话的权威状态、播放/暂停/停止控制、音量控制以及与应用生命周期的协调。Addition 负责播放器工作区、音乐库和播放列表等用户体验。Core 必须能够在 Addition UI 不处于前台时响应用户已授权的播放控制，例如快速暂停；不能要求播放器页面始终可见才能暂停音乐。

音频播放服务的具体设备选择、格式解码库、后台持续播放策略和主窗口关闭后的行为由后续实现与 Feature Spec 验证，不在本 ADR 中预设未经确认的细节。Core 的正常启动和普通游戏启动不得依赖 Music Player 或其 Java Agent。

### 3. 使用随 Addition 提供的独立 Java Agent 作为 Minecraft Audio Bridge

游戏内音量控制由一个独立 Java Agent 提供，作为 `ecl.music-player` 的配套运行时组件，而不是单独登记为另一个 Addition，也不是 Fabric、Forge 或 NeoForge Mod。

- ECL 本身继续使用现有技术栈；不要求 ECL Core 引入 Java 业务层。
- ECL 在用户启用并授权游戏内音量集成后，仅对用户明确选择的目标 Minecraft 实例，在其 JVM 启动参数中注入 Agent。
- Agent 在目标 Minecraft JVM 内运行，通过受控本地通信与 ECL Core 建立实例/会话绑定的控制通道。
- Agent 负责定位当前 Minecraft 客户端、将控制操作调度到客户端线程，并调用对应版本可用的游戏内音量设置入口。
- Agent 不承担音乐解码、音频播放、音乐库扫描、播放器 UI 或通用脚本执行职责。
- 默认采用 JVM 启动时注入；不得为了已运行的游戏而静默附加到任意进程。是否支持运行中附加必须由单独的安全评审决定。
- Agent 不是完整操作系统沙箱。它在目标 JVM 内执行，具有目标游戏进程相同的操作系统用户权限；不得把“预期只调整音量”描述成技术上只能访问音量。

### 4. 不按 Mod Loader 拆分三套 Agent

Audio Bridge 的产品契约与控制语义统一，不以 Fabric、Forge、NeoForge 分别建立三套独立 Agent 或三套 Addition。

- 版本差异通过受控的 Minecraft 版本适配层处理，包括类/方法定位、映射命名空间、客户端对象发现、API 形式和线程调度。
- Vanilla、Fabric、Forge、NeoForge 只有在相应版本与环境完成实机验证后，才能列入兼容范围。
- 不得仅因某个 Loader 使用 LaunchWrapper、ModLauncher 或其他启动链，就预先断定必须开发 Loader 专属 Mod；也不得假设单一 Agent 已天然兼容这些启动链。
- 旧版和新版 API 可以使用不同 Adapter，但上层播放状态、授权和控制语义保持统一。
- Agent 应尽可能兼容 ECL 支持的旧 Minecraft Java 运行时；Java 字节码目标和依赖选择必须在原型阶段验证，不能以较新的 Java 运行时要求破坏旧版实例兼容性。
- 无法可靠识别版本、映射或客户端对象时，Agent 必须报告不兼容并停止游戏内控制，不得猜测调用或阻止 Minecraft 正常运行。

### 5. 明确游戏音量控制语义与边界

当用户启用游戏内音量集成且 Agent 正常连接时，目标行为为：

| ECL Music Player 状态 | Minecraft `MUSIC` 分类音量 |
|---|---:|
| ECL 音乐正在播放 | `0.0` |
| ECL 音乐暂停 | `1.0` |
| ECL 音乐停止或释放控制 | `1.0`，随后按会话清理规则处理 |

- 仅控制 Minecraft 的 `MUSIC` 音量分类，不主动修改 `RECORDS`、`WEATHER`、`BLOCKS`、`HOSTILE`、`NEUTRAL`、`PLAYERS`、`AMBIENT` 等其他分类。
- 通过游戏内运行时 API 修改当前客户端状态；不得把运行时写入 `options.txt` 当作实时控制机制。
- 使用 `MUSIC` 分类的原版或模组声音会受到该分类音量影响；使用其他分类播放的模组声音不保证被静音。产品文案不得承诺消除所有模组音源。
- Agent 应在会话开始时识别并记录接管前的用户音量，在正常释放/退出清理时避免把 ECL 的临时覆盖值永久写成用户偏好。暂停状态按用户明确要求设为 `1.0`；正常会话结束时的原值恢复与保存时序必须经过实机验证。
- 游戏内用户手动调整音量、多个 Minecraft 实例并行运行、Core 失联、Agent 异常退出和游戏崩溃都必须有明确处理；不能让一个实例的控制指令影响另一个实例。

### 6. 将游戏进程修改作为独立、明确的用户授权

Java Agent 被注入目标 Minecraft JVM，属于对 Minecraft 进程的实际修改。此行为不得隐藏在普通安装确认、模糊的“音频访问”权限或默认启用选项中。

安装/启用授权流程必须遵循 ADR 0053，并在用户作出授权决定前，以独立、不可折叠且高显著性的警示区域完整展示以下原文，不得删减、弱化、改写或仅放入二级详情：

> **此扩展会更改Minecraft进程**  
> **ECL 音乐播放器依靠Java Agent更改游戏内音量，以避免多音源冲突**

该警示必须在权限授权弹窗中明显区别于普通说明，用户无需展开更多内容即可看到。授权按钮仍遵循 ADR 0053 的阅读缓冲和主动确认规则；警示本身不得被折叠、默认隐藏或被普通权限列表淹没。

权限语义必须明确区分：

- 用户授权的是 ECL 将随指定实例启动并在其 Minecraft JVM 中运行 Audio Bridge，以实现游戏内音量控制。
- 授权仅适用于明确的 Addition 身份、版本/Agent 完整性身份及用户选择的实例范围；新增或改变进程修改范围时必须重新评估授权。
- 不得对未选择的实例、其他 Java 进程或已运行的 Minecraft 进程进行隐式注入。
- 用户拒绝或撤销游戏进程修改授权后，Core 不得注入 Agent。播放器仍可使用 Core 音频服务播放本地音乐，但游戏内 BGM 自动控制不可用，并应向用户说明可能出现多音源同时播放。
- Agent 包必须经过发布者身份、签名/完整性和版本兼容性检查。校验失败时不得注入；用户同意权限不能覆盖完整性或来源验证失败。
- Core 与 Agent 的通信只暴露完成音量协调所需的最小命令，不提供通用代码执行、任意文件访问或通用网络代理接口。

授权弹窗解释的是 Agent 的预期用途，不代表 Agent 受到完整沙箱限制。由于 Agent 在 Minecraft JVM 内执行，不能仅依靠权限 UI 或 IPC 约定声称它无法访问同一用户权限下的其他资源。

### 7. 安全降级与故障隔离

- Agent 缺失、未授权、不兼容、连接失败或运行异常时，Music Player 的本地音乐播放能力仍可独立工作。
- 游戏内音量控制不可用时，不得通过修改 `options.txt`、注入其他未知组件或切换到未经授权的进程控制方式进行静默补偿。
- Agent 不得成为普通 Minecraft 启动的必需依赖；用户未启用 Music Player 时，不注入、不下载、不初始化 Agent。
- Agent 故障不得导致 ECL Core 或 Minecraft 被主动终止；Core 应停止发送无效控制并呈现功能降级状态。
- 正常游戏退出、Music Player 停止/释放控制及 ECL 生命周期结束时，应尽可能执行有序清理；异常退出场景不得依赖 `options.txt` 外部写入作为恢复机制。

## 备选方案

### 方案 A：运行时直接修改 `options.txt`

不采用。Minecraft 运行时持有内存中的音量状态，外部修改配置文件不能可靠地通知当前客户端立即更新，且游戏后续保存选项时可能覆盖外部修改。

### 方案 B：按 Fabric、Forge、NeoForge 分别开发 Mod

不作为首选。Loader Mod 能提供游戏内 API 访问，但会形成多套构建、发布、兼容性和维护链路。只有当 Agent 原型证明某个 Loader 的启动/类加载环境无法通过统一方案支持时，才考虑增加专用后端；不能预先将三套 Mod 作为必需设计。

### 方案 C：通过 Windows 音频会话控制整个 Minecraft 进程音量

不采用为主要方案。它无法只修改 Minecraft 的 `MUSIC` 分类，会同时影响脚步声、方块声、战斗声等游戏音效，不满足保留游戏声音的目标。

### 方案 D：由 Core 托管播放，配套单一 Java Agent 提供游戏内分类音量桥接

采用为当前 Proposed 架构方向。它将播放器 UI、音频输出和 Minecraft 进程适配分离，同时避免 ECL 主程序引入 Java 业务层，并保留跨版本/Loader 适配空间。Agent 的可行性和覆盖范围仍须通过原型验证。

## 后果

### 正面影响

- ECL 可以在保留现有技术栈的同时提供本地音乐播放。
- Core 能够统一管理播放状态，并在 Addition UI 不可见时响应暂停控制。
- 游戏内音量通过 Minecraft 自身运行时接口调整，不依赖不可靠的外部配置文件写入。
- 单一 Agent 与版本适配层避免默认维护三套 Loader Mod。
- 用户能够在授权前明确知道 ECL 将修改目标 Minecraft 进程，以及 Java Agent 的用途。
- 未授权或不兼容时仍可播放音乐，不影响普通游戏启动。

### 代价与风险

- Java Agent 进入目标游戏 JVM，具有与游戏进程相同的操作系统权限；必须严格治理代码来源、签名、完整性、更新和授权范围。
- Minecraft 混淆映射、不同版本 API、不同 Loader 的类加载器和启动链会带来持续兼容性成本。
- 运行时音量状态、游戏选项持久化和异常退出之间存在竞态，需要实机验证。
- 音量分类只覆盖使用 `MUSIC` 类别的声音，不能保证所有模组音乐都遵循该分类。
- Core 音频服务需要处理设备不可用、解码失败、并发播放、资源释放和应用退出等生命周期问题。
- 如果无法验证 Agent 的目标环境兼容性，必须接受部分实例不支持自动 BGM 控制，而不能降低安全要求。

## 验收标准

本 ADR 当前为 Proposed。以下为后续原型和实现的验收方向，不表示功能已经实现：

1. Registry 中存在唯一 `ecl.music-player` Feature Addition 登记，状态与 ADR/Manifest 一致，且没有正式发布资产时不得标记为 Available。
2. ECL Core 能在不依赖 Music Player Addition UI 存活的情况下管理音频播放会话，并提供可供授权 Addition 使用的受控播放/暂停/停止能力。
3. ECL 本身不需要引入 Java 业务层；Agent 仅在用户授权并选择目标实例后作为该实例 JVM 的启动参数注入。
4. Vanilla 1.7.10、Forge 1.7.10、Vanilla 1.12.2、Forge 1.12.2 至少完成同一 Agent 原型验证；未通过的环境不得声明兼容。
5. 现代版本与 Fabric、NeoForge 等环境按独立验证矩阵逐项测试；不得从单一版本或 Loader 推断全范围兼容。
6. Agent 能可靠识别目标 Minecraft 客户端和版本，且所有音量变更均在客户端线程执行；识别失败时安全禁用桥接。
7. 播放状态为 Playing 时将 `MUSIC` 设为 `0.0`；Paused 时设为 `1.0`；其他声音分类保持不变。
8. 测试确认使用 `MUSIC` 分类的声音受控，使用其他分类的声音不会被误称为已控制。
9. 多实例同时运行时，每个 Agent 只接受与自身实例/会话绑定的 Core 控制，不发生跨实例误控。
10. 用户拒绝或撤销进程修改授权后，ECL 不注入 Agent；播放器仍能播放音乐并明确说明游戏内音量自动控制不可用。
11. 授权弹窗在用户作出决定前，以不可折叠的高显著性区域完整显示规定的两行警示原文，并遵循 ADR 0053 的授权缓冲与主动确认要求。
12. Agent 的签名/完整性或兼容性验证失败时不注入；权限授权不能覆盖验证失败。
13. Agent 不提供通用代码执行、任意文件访问或通用网络代理接口；不得宣称其具有完整操作系统沙箱。
14. Agent 缺失、断连或崩溃不会阻止 Minecraft 正常启动，也不会导致 ECL Core 被终止。
15. 游戏正常退出与桥接释放时能清理临时音量覆盖；验证异常退出及游戏选项保存时序，不把临时覆盖值意外持久化为用户偏好。
16. 用户不使用 Music Player 时，普通游戏启动不注入、不初始化或等待 Agent。

## 修订记录

- 2026-11-09：首次提出 ECL Music Player 与 Minecraft Audio Bridge 架构。确定 Core 托管音频播放、单一 Java Agent 随 Addition 提供、按版本适配而不默认拆分 Loader Mod，并规定 Minecraft 进程修改授权弹窗必须高显著性展示用户指定的两行警示。Decision 保持 Proposed，Implementation 保持 Not Started。