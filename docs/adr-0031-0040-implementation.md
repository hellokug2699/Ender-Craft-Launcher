# ADR 0031–0040 实施记录（2026-11-03）

本页记录本轮按 ADR 0031–0040 落地的实现、可复核证据与已知缺口。状态严格遵循 [ADR README](adr/README.md)：`Implemented` 表示约定范围已有实现但验收未全部完成；`In Progress` 表示范围尚未完成。

| ADR | Decision | Implementation | 本轮结果 |
|---|---|---|---|
| [0031 多维度世界](adr/0031-multi-dimension-world-architecture.md) | Accepted | Implemented | 维度身份/存储解析、维度感知身份与地图切换 |
| [0032 Additions](adr/0032-native-feature-interfaces-and-optional-additions.md) | Accepted | In Progress | 目录签名、依赖规划、安装事务、按需加载 |
| [0033 本地诊断](adr/0033-local-minecraft-diagnostic-and-knowledge-engine.md) | Accepted | In Progress | 本地解析、知识库与证据链 |
| [0034 自完整性](adr/0034-ecl-self-integrity-and-recovery.md) | Accepted | In Progress | 校验、Repair Mode、本地恢复缓存 |
| [0036 受限知识浏览器](adr/0036-embedded-browser-for-minecraft-knowledge.md) | Accepted | In Progress | 白名单/重定向/子资源策略与本地错误页 |
| [0037 账户外观与衣柜](adr/0037-account-appearance-and-local-wardrobe.md) | Accepted | In Progress | 本地衣柜、PNG 校验、Provider 能力模型 |
| [0038 Authlib 材质上传](adr/0038-authlib-injector-texture-upload-protocol.md) | Accepted | In Progress | 能力声明、PUT/DELETE 协议与上传后复查 |
| [0039 运行时生命周期](adr/0039-application-runtime-lifecycle-and-graceful-shutdown.md) | Accepted | Implemented | 关闭协调器、WPF/CLI 接入与超时测试 |
| [0040 Vanilla Toolkit](adr/0040-vanilla-toolkit-addition.md) | Accepted | In Progress | 数据模板、数据包校验、Registry 查询 |

> 本表是实施状态，不是发布状态。`Implemented` 不等于 stable 发布，也不代表所有验收项完成。

## ADR 0031：多维度世界架构

已落地：

- `DimensionId`：逻辑身份与磁盘路径分离，规范化校验、原版/自定义区分，拒绝大小写歧义与 `..` 路径。
- `DimensionDiscoveryService`：只读维度发现与存储解析；支持传统 `region/`、`DIM-1/`、`DIM1/`，数据包 `dimensions/<namespace>/<path>/`，以及维度级 `data/`；Region、Entities、POI、Data 的状态与依据独立表达。
- 版本规则：1.13 独立 Entities、1.14 POI、1.16 自定义维度布局；`level.dat` 的 `WorldGenSettings.dimensions` 作为注册证据。
- 未知/冲突降级：同 ID 多目录标记 `Conflicted` 并拒绝读取；未注册自定义目录标记 `Unconfirmed`；无法识别 ID 的目录进入候选列表，不映射到主世界；重解析点拒绝跟随。
- 身份隔离：`WorldDimensionKey`、`RegionIdentity`、`ChunkIdentity`、`BlockIdentity`；`WorldChunkReader` 暴露 World+Dimension 身份。
- 实例工作台：维度下拉框显示 ID、显示名与存储状态，优先选择可读维度；切换世界/维度/图层会取消上一次渲染并丢弃迟到结果；缺失、未确认、冲突维度不会回退到其他维度。
- 诊断联动：诊断事件新增世界目录名与维度身份；只有明确、非推断且与当前维度一致的事件才生成地图标记；缺少维度身份的事件保持未定位。

验证：

- `tests/ECL.Tests/World/DimensionDiscoveryTests.cs`（11 项）：身份解析、原版三类存储、已注册/未注册数据包维度、冲突、未知目录候选、注册但无存储、旧版本规则、损坏 level.dat、WorldSave 投影、跨维度/跨世界身份隔离。
- `tests/ECL.Tests/World/RealSaveDimensionVerificationTests.cs`：设置 `ECL_WORLD_VERIFY_DIR` 指向存档父目录后，对真实存档做只读解析并断言不会猜测或跨维度复用。
- 2026-11-03 只读实测：1.20.1 五个原版存档均解析出主世界、下界、末地，Region/Entities/POI/Data 状态按实际目录分别表达；一个 1.21.1 模组整合包存档解析出 `pixelmon:debug`、`pixelmon:ultra_space`（level.dat 注册，仅存在 `data/`，区块存储标记 Missing）；未注册自定义目录保持 Unconfirmed。测试未写入任何存档，也未提交本机路径。

已知缺口：

- 未确认维度仅提供元数据与存储状态，尚未开放“原始/结构只读视图”；自定义维度语义图层仍未知。
- 世界编辑器目前只有只读地图与 NBT 浏览；ADR 0031 §5 的维度化编辑工作上下文需在写入路径落地后复核。
- 工作台维度概览尚未做独立面板与手工验收；诊断事件的 World 身份目前取自实例分析上下文，不能从日志单独恢复世界目录名。

## ADR 0032：原生功能入口与 Additions

已落地：

- `ECL.Additions`：`AdditionId`、类型、版本、依赖、Core API 范围、平台、撤回标记、数据 schema。
- 官方目录：`additions/catalog.json` + detached RSA-PSS 签名 `catalog.json.sig`；公钥在 `build/additions-trust.json`，私钥只在私有 Core 仓库。
- 目录服务：远程获取失败/离线时回退最近一次通过签名校验的缓存并明确标记“可能不是最新”；缓存本身仍验证签名。
- 规划器：下载前解析依赖闭包，拒绝循环、缺失、版本不满足、Core API 不兼容、平台不匹配与冲突；可选依赖不静默安装；已安装且满足范围的依赖不强制升级；安装/更新共享依赖优先顺序，避免先更新依赖方。
- 安装器：包大小+SHA-256 → 安全解包 → `addition.json` 逐文件校验 → staging 原子移动到版本目录 → 状态原子写入；更新保留上一版本；失败删除新版本并保留原可用版本；卸载检查依赖并把包目录移入隔离区后再删状态，保留 `additions/data/<id>` 用户数据。
- 状态：`Available/Downloading/Installing/Installed/Enabled/UpdateAvailable/Incompatible/Broken` 与运行时 `Loaded` 分开；安装不等于启用或加载。
- 按需加载：`IAdditionModule` + 可回收 `AssemblyLoadContext`；只传递最小 `AdditionHostContext`（ID、版本、Core API、数据目录），不暴露启动器对象/UI。

验证：

- `tests/ECL.Tests/Additions/AdditionTests.cs`（14 项）：目录校验、依赖顺序、循环/缺失/不兼容/平台、已安装依赖复用、安装、篡改包拒绝、更新/回滚、依赖卸载保护与用户数据保留、按需加载、禁用/不兼容拒绝、签名目录与离线缓存、官方目录签名校验。
- `tests/ECL.TestAddition` 是测试用最小扩展，验证真实程序集加载与数据目录隔离。

已知缺口：

- 官方目录暂为空；Vanilla Toolkit 尚未发布首个 Release Asset，因此用户暂时无法从页面安装任何扩展。
- WPF 原生 Additions 管理页/功能入口、未安装/部分安装状态、目录刷新与撤回 UI 尚未实现；普通启动路径未接入目录读取，这一点符合“不得阻塞启动”，但功能入口仍缺失。
- 扩展加载尚未接入应用生命周期关闭顺序（`AdditionRuntime` 提供 `ShutdownAllAsync`，App 尚未注册调用）。

## ADR 0033：本地诊断与知识引擎

已落地：

- `LatestLogParser` / `CrashReportParser` / `DiagnosticInputLoader`：把 latest.log、崩溃报告归一到带文件、1 基行号、时间、线程、级别、logger、异常类型与堆栈续行的事件模型；堆栈不会拆成独立事件，损坏/超限输入只影响对应文件。
- `KnowledgeBaseLoader`：版本化 JSON schema（schemaVersion、knowledgeVersion、rules、sources），校验缺失字段、非法正则、重复规则、未知字段与不兼容 schema；坏文件返回明确错误，不抛异常阻塞宿主。
- `DiagnosticEngine`：规则按正则/必需片段/最小次数匹配，产出证据链（文件、行号、摘录）、规则版本、知识库版本、置信度（直接证据/高度疑似/无法确定）、严重度、解释与建议；`disabled` 规则不参与；仅凭“最后一行是某异常”不会自动判定根因。
- `DiagnosticHistoryStore`：本地 JSON 历史，原子写入、schema 版本、损坏容错，报告保留当时使用的知识库版本与实例环境摘要；不保存不必要的原始日志副本。
- 全部分析在本地完成，无网络调用、无上传路径。

验证：

- `tests/ECL.Tests/Diagnostics/DiagnosticEngineTests.cs`（11 项）：多行堆栈行号、规则命中证据与置信度、异常行不作为确定根因、知识库缺字段/坏正则/schema 不兼容、损坏文件容错、disabled 规则、历史记录 round-trip 与损坏容错。

已知缺口：

- 知识库尚未通过 ADR 0032 的 Data Addition 下载/更新流程交付，当前由调用方提供本地文件。
- 原生诊断中心页面、实例级诊断历史 UI、环境/Mod 关系分析、ADR 0030/0031 世界诊断事件联动尚未接线。
- 外部 AI 解释与用户主动分享未实现（本就不是必需依赖）。

## ADR 0034：ECL 自身完整性与可恢复安装

已落地：

- `SelfIntegrityVerifier`：以当前版本目录（回退安装根目录）的签名 `update-manifest.json` 为基准，逐文件核对存在性、大小与 SHA-256；单独区分缺失、大小不符、哈希不符、不可读、路径越界与版本指针不一致。
- 来源信任：复用 `update-trust.json` 与 RSA-PSS 验签；缺失/无效/签名失败分别报告，文件完整但来源不可信不会被伪装成“已验证”。
- 轻量启动检查：正常启动只检查存在/大小；仅在内容受损时进入 Repair Mode，避免每次全量哈希；清单缺失的旧版安装明确跳过并提示。
- Repair Mode：`ECL.Bootstrapper.exe --verify` / `--repair`；只恢复清单拥有的 ECL 文件，绝不删除实例、世界、配置或日志；无本地材料或来源不可信时明确失败（退出码 6）。
- Recovery Cache：按版本保存已验证副本（签名清单 + 文件），按创建时间保留最近 N 个版本；缓存使用前重新验签与哈希，不因位于本地就被信任。
- 联网修复：当本地材料不足且 `ECL_UPDATE_FEED` 指向 HTTPS 更新源时，从该源获取与当前版本精确一致的签名完整包，验证清单、包哈希与逐文件哈希后只覆盖损坏文件；版本不一致或任何校验失败都拒绝替换。
- 部署来源：Installer/更新部署把已签名更新清单保存到安装根/版本目录，作为后续校验依据。

验证：

- `tests/ECL.Tests/Deployment/SelfIntegrityTests.cs`（9 项）：签名安装验证、缺失/哈希区分、轻量只查大小、从缓存修复、无缓存显式失败、坏签名拒绝修复、未信任来源拒绝缓存、修复不触碰用户数据、缓存数量上限。
- `tests/ECL.Tests/Deployment/RemoteRepairTests.cs`（3 项）：远程恢复损坏文件且不触碰用户数据、版本不一致/篡改包拒绝、未信任安装拒绝联网修复。
- `tests/ECL.Tests/Deployment/BootstrapperCommandLineTests.cs`、`BootstrapperExitCodesTests.cs`：`--verify`/`--repair` 解析、互斥与退出码 6。

已知缺口：

- 远程修复需要显式配置 `ECL_UPDATE_FEED`；尚无基于设置页/更新源的自动发现与用户确认流程。
- `install-manifest.json` 仍只服务卸载，尚未独立签名；当前来源信任基于更新清单副本。
- Bootstrapper 自身损坏/缺失的恢复链未覆盖（仍需要独立安装介质或重新安装）。

## ADR 0036：受限知识浏览器

已落地：

- `KnowledgeSitePolicy`：仅 HTTPS；hostname 先按 IDN、大小写、尾随根点规范化，再按 DNS 标签边界判断 `zh.minecraft.wiki` / `mcmod.cn` 域名树；拒绝 userinfo 欺骗、非 HTTPS、`file/data/javascript` 等协议；port 不改变归属判断。
- 逐跳重定向：只处理 301/302/303/307/308，先解析 `Location`（相对地址相对当前 URL）再校验；外部目标、非 HTTPS、缺失/非法 Location 一律拒绝，并保留真实 3xx 状态码、原始请求地址与被拒目标。
- 子资源与 iframe 使用同一白名单；顶层导航、子资源、新窗口共用策略。
- `KnowledgeErrorPage`：本地生成 HTML，动态文本 HTML 编码并限长，无外部资源、无脚本、无自动跳转。
- `KnowledgeAddressBarState`：只读、可复制完整 URL、不可编辑/粘贴/搜索。

验证：

- `tests/ECL.Tests/Knowledge/KnowledgeSitePolicyTests.cs`（92 项，含参数化）：ADR 验收清单中的允许/拒绝域名样例、大小写/尾随点/IDN/userinfo、非 HTTPS、跨域名树站内跳转、逐跳重定向与状态码保留、子资源、错误页注入与只读地址栏。

已知缺口（阻塞项）：

- 未实现 WebView2 宿主。ADR 0036 §5 要求在跟随重定向前取得原始 3xx 并在网络响应层取消；仅靠导航事件无法保证这一点。宿主必须验证运行时能力后才能宣称完成浏览器集成，当前不启动/不初始化 WebView。
- 浏览器用户数据目录、Cookie 生命周期、运行时缺失降级、下载/弹窗/权限默认拒绝仅停留在策略契约，尚无宿主实现与实机验收。

## ADR 0037 / 0038：账户外观、衣柜与材质上传

已落地：

- 本地衣柜 `WardrobeStore`：PNG 导入前校验，按 SHA-256 去重，索引 `index.json` 原子写入；索引损坏时返回空索引与警告并可继续导入；删除前通过引用检查回调拒绝删除仍在使用的资源；账户/实例数据与资源分离，不保存令牌。
- 独立 PNG 校验/解码器：签名、CRC、IHDR、文件/尺寸/像素上限、zlib 解码与反滤波；64x64 皮肤按外侧手臂列透明度做 Classic/Slim 启发式，无法确定返回 Unknown 并解释“模型本质是账户元数据”。
- 能力模型：`AppearanceCapability` 标志与 `AppearanceCapabilityReport`；`AppearanceOperationStatus` 区分 Succeeded/Unsupported/Unauthorized/NetworkFailed/InvalidInput/RedirectRejected/ServerRejected。离线 Provider 只承诺本地能力；Microsoft Provider 不重复 OAuth、不保存第二份令牌，在逐项验证外观接口前所有账户外观操作返回 Unsupported。
- Authlib 材质协议：解析角色资料 `uploadableTextures` 分别得到 skin/cape 能力；严格 HTTPS、Bearer、`PUT/DELETE /api/user/profile/{uuid}/skin|cape`、multipart、model 仅用于皮肤且仅在明确选择时发送；上传前 PNG 校验；上传后重新读取资料并用 `RequestAccepted / ProfileUpdated / GameVerificationPending` 三阶段表达；错误信息脱敏，令牌不出现在消息/细节中。

验证：

- `tests/ECL.Tests/Wardrobe/WardrobeTests.cs`（8 项）：PNG 有效/坏签名/坏 CRC/超尺寸、Slim/Classic/Unknown 启发式、导入去重与引用保护、索引损坏恢复、Offline/Microsoft 能力与 Unsupported、`uploadableTextures` 解析、Bearer/路径/multipart/model、重定向拒绝、未声明能力不上传、坏 PNG 不发请求。

已知缺口：

- 没有可用的第三方认证测试服务端，未完成真实上传验收；按用户指示跳过实机第三方上传。协议行为由 fake handler 覆盖。
- Microsoft 外观接口未逐项实测，Provider 保持 Unsupported；原生衣柜页面、实例外观应用、皮肤预览渲染与账户外观同步 UI 未实现。
- 资源包覆盖、实例级外观覆盖持久化与“游戏内已验证”状态采集尚未接入。

## ADR 0039：应用运行时生命周期与优雅关闭

已落地：

- `ECL.Core.Runtime`：`ApplicationLifecycleCoordinator` 状态机 `Created → Starting → Running → Stopping → Disposing → Stopped`，启动失败进入 `Faulted` 并在有界时间内清理。
- 后台工作登记：ID、所属模块、类别、单项超时、协作取消令牌、可等待完成句柄；异常被观察并记入报告，fire-and-forget 不再裸奔。
- 关闭顺序：停止接收新工作 → 取消并等待受管理工作 → 按登记逆序执行异步停止步骤 → 释放登记资源 → 汇总超时/失败；总预算限制整体关闭时间。
- 幂等、防重入；WPF `Closing` 先取消本次关闭，等待关闭完成后再 `Shutdown()`；`OnExit` 仅做最多 2 秒的同步兜底。
- CLI 在退出前等待受管理工作收尾；启动清理任务改为登记工作。

验证：

- `tests/ECL.Tests/Core/ApplicationLifecycleTests.cs`（9 项）：状态顺序、取消、幂等、超时不响应、停止步骤逆序与失败继续、资源逆序释放、启动失败保留原异常、工作异常观察、关闭后拒绝登记、Created 状态清理。
- `dotnet run --project src/ECL.Cli -c Release -- version` 冒烟通过；构建零警告。
- 2026-11-03 WPF 冒烟：以临时数据目录启动 ECL，关闭主窗口后进程退出码 0；日志记录「开始关闭：1 项后台工作，1 个停止步骤」与「应用已优雅关闭」。未进行 Minecraft 游戏进程并行退出验收。

已知缺口：

- WPF 主窗口关闭与优雅退出已完成冒烟；Minecraft 游戏进程并行运行时的独立性、ADR 0015/0019 游戏会话宿主登记仍待实机验收。
- 下载/安装模块没有全部登记关闭策略，仍依赖各自局部生命周期。

## ADR 0040：Vanilla Toolkit

已落地：

- `additions/vanilla-toolkit/data-template/`：Manifest 示例（含真实大小/SHA-256）、`addition.json` 示例、数据包与 registry 数据域 JSON Schema、1.20.1 最小样例、发布流程说明。
- `ECL.VanillaToolkit`：数据包加载与路径/大小/SHA-256/schema 校验；显式 Minecraft 版本集合，版本不支持时明确失败，不回退“最接近版本”。
- Registry 查询：按 ID/名称/别名/部分匹配，支持 kind 过滤与语言选择，结果携带数据质量与来源；条目支持 `introduced`/`removed`/`versions` 适用性。
- 本地资源：只从与目标版本精确同名、且存在客户端 jar 的实例目录读取 `assets/minecraft/textures/`；无匹配实例时返回“没有可用的版本实例”，不内置/不联网/不跨版本借用。

验证：

- `tests/ECL.Tests/VanillaToolkit/VanillaToolkitTests.cs`：仓库模板可加载并查询中文名、版本不支持被拒、哈希/路径篡改拒绝、版本适用性、本地纹理精确版本与缺失资源。
- 官方目录签名见 `tests/ECL.Tests/Additions` 中的目录验证。

已知缺口：

- 尚未发布首个 Vanilla Toolkit 扩展包与目录条目；工具页面、指令/附魔/矿石等数据域与跨版本比较未实现。
- 资源解析尊重实例资源包优先级尚未实现；当前读取客户端 jar 原始纹理，UI 必须如实标注来源。
- 首期仅原版规则；未提供模组规则支持。

## 验证与环境

```powershell
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
python scripts/check_docs.py
pwsh -NoProfile -File scripts/check-commit-hygiene.ps1 -All
```

真实存档验证（可选，路径只来自环境变量）：

```powershell
$env:ECL_WORLD_VERIFY_DIR = "<包含多个存档目录的父目录>"
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release --filter "FullyQualifiedName~RealSaveDimensionVerification"
Remove-Item Env:\ECL_WORLD_VERIFY_DIR
```

## 隐私与安全

- 诊断、存档、世界、Seed 与账户数据默认只在本机处理；本轮没有新增上传路径或遥测。
- 目录/恢复材料/扩展包均在使用前校验签名与哈希；无法验证时拒绝使用并保留原有可用版本。
- 未新增仓库、未建设在线目录/代理服务；扩展包仍通过现有公有仓库 Release Asset 分发。
