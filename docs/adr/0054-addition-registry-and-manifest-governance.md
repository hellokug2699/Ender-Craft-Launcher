# ADR 0054：Addition 注册表、清单与生命周期治理

- Decision: Accepted
- Implementation: Not Started
- Date: 2026-11-04
- Scope: 官方 Addition 的身份分配、统一注册目录、机器可读 Manifest、登记审核、版本发布、兼容性、生命周期与历史 ADR 迁移
- Related: [ADR 0032：原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0053：Additions 能力权限模型与用户授权流程](0053-addition-capability-permission-and-user-consent.md)

## 背景

ADR 0032 已经确立 ECL 的 Additions 扩展体系，并要求官方目录描述稳定 Addition ID、名称、类型、版本、下载资产、校验信息、发布者签名、Core API 兼容范围、依赖、冲突、数据 Schema、适用环境及撤回信息。后续 ADR 又分别提出了原版工具集、性能监控、网络诊断和 GitHub 项目中心等扩展方向。

随着扩展数量增加，当前以功能 ADR 分散描述扩展身份和功能的方式会产生几个问题：

- 用户无法从一个固定入口浏览全部官方 Additions，也难以判断某项能力是否已正式注册、是否可安装。
- 稳定 ID、名称、版本、依赖、权限和发布资产可能散落在多个 ADR 中，容易出现重复、遗漏或互相矛盾。
- ADR 同时承担架构决策和产品目录的职责，导致功能 ADR 的状态变化被误解为扩展发布状态。
- 新 Addition 的提出、审核、登记、发布、弃用和撤回缺少统一流程。
- 已经存在的扩展 ADR 容易被误当作当前官方扩展清单，历史提案与实际可安装资产难以区分。

ECL 需要将“扩展注册”从分散的架构决策文档中独立出来。该注册目录仍位于现有公有仓库，不新增仓库或强制在线服务；目录由一个 README 统一统领，并遵守 ADR 0032 的扩展架构和本 ADR 的治理规则。

## 决策

### 1. 建立唯一的官方 Addition 注册目录

在现有 ECL 公有仓库建立 `docs/additions/`，并以其中的 `README.md` 作为全部官方 Additions 的统一注册目录和人工阅读入口。

目录采用以下组织方式：

```text
docs/additions/
├── README.md
└── <addition-id>/
    ├── README.md
    └── manifest.json
```

- `docs/additions/README.md` 是官方 Addition 总目录，列出所有已登记项目及其状态，并链接到各 Addition 的说明页。
- `docs/additions/<addition-id>/README.md` 记录该 Addition 面向用户和维护者的功能说明、适用范围、限制、依赖、权限摘要、兼容性及相关 ADR。
- `docs/additions/<addition-id>/manifest.json` 是对应 Addition 的机器可读登记清单，描述身份、版本、依赖、能力、权限、兼容性和发布资产等元数据。
- 大型二进制包和数据包不得提交到 Git 文档目录；按 ADR 0032 的约定使用现有仓库的 Release Assets 等发布渠道。
- 除非未来有明确的技术理由并经新 ADR 决策，不再建立第二份与总 README 并列的官方扩展目录。

README 是官方扩展的发现与浏览入口；Manifest 是供 Core 和发布工具读取的机器可读契约。两者不是互相替代的重复文件，而是面向不同使用者的同一登记记录。它们必须保持一致。

### 2. Addition 注册与 ADR 架构决策分离

ADR 负责记录长期架构决策、边界、约束、风险及验收方式；Addition Registry 负责记录正式扩展身份、功能说明、当前状态和发布信息。

因此：

- 新增或更新 Addition 时，必须维护 Registry 和 Manifest。
- 不得仅凭创建了一份功能 ADR，就宣称该扩展已经注册、已经发布或可以安装。
- 不得将全部 ADR 自动转换为 Addition；只有明确采用 ADR 0032 的可选扩展交付模式、具有独立 Addition 身份和生命周期的能力才登记。
- 一个 Addition 可以关联多份 ADR；一份 ADR 也可以关联多个 Addition。
- Addition 的登记状态、发布版本和可安装性不得由关联 ADR 的 Decision / Implementation 状态推导。
- Registry 中的条目必须反向链接其架构 ADR、Feature Spec 或其他必要说明；相关 ADR 也应链接回 Registry 对应条目。

### 3. 分配稳定且不可复用的 Addition ID

每个官方 Addition 必须拥有全局唯一、稳定且不可复用的 ID。

- ID 创建后不得因名称、描述、页面、实现语言或维护者变化而更改。
- 已撤回或永久停止分发的 ID 不得分配给其他 Addition。
- ID 应使用小写、稳定、便于机器处理的命名形式；官方命名空间使用 `ecl.` 前缀。
- ID 不得绑定某个 UI 页面、导航位置或具体 ViewModel。一个 Addition 可以服务多个原生页面，一个页面也可以依赖多个 Addition。
- ID 分配前必须检查 Registry 中现有条目，避免重复或语义冲突。
- 未完成评审的候选项目可以使用临时提案标识，但不得提前占用正式 Addition ID。

正式 ID 示例（仅示意，不代表已经分配）：`ecl.vanilla-toolkit`。

### 4. Registry 与 Manifest 的最低字段

每个正式登记条目至少包含以下信息；不适用的字段应明确标记为不适用，不得用猜测值填充。

| 字段 | 要求 |
|---|---|
| Addition ID | 稳定唯一标识 |
| Name | 面向用户的正式名称 |
| Type | `Feature` 或 `Data`，遵循 ADR 0032 |
| Category | 统一分类，便于目录浏览 |
| Summary / Description | 用途、主要能力及明确边界 |
| Registry Status | 本 ADR 定义的登记生命周期状态 |
| Current Version | 当前登记的版本；尚未发布时明确标记 |
| Core API Compatibility | 支持的 Core API 范围 |
| Dependencies | 必需、可选依赖及冲突 |
| Capabilities / Permissions | 所需宿主能力及权限，遵循 ADR 0053 的模型；0053 尚未 Accepted 前不得将其提案细节描述为已实施约束 |
| Environment Compatibility | 适用的 Minecraft、Loader、平台或其他环境（如适用） |
| Data Schema | Data Addition 的数据格式及 Schema 版本（如适用） |
| Release Assets | 发布版本、下载地址、包大小、SHA-256 与签名信息 |
| License / Data Sources | 代码许可证、数据来源及许可（如适用） |
| Related Documents | 功能说明、ADR、Feature Spec 和维护说明 |
| Lifecycle Metadata | 首次登记、最近更新、弃用/撤回说明及替代项（如适用） |

README 应优先呈现用户决策所需的信息，不应把完整机器字段堆叠成难以阅读的大表。Manifest 必须提供 Core 和发布工具所需的完整、可校验字段。

### 5. Manifest 必须可校验并与发布资产一致

Manifest 应使用明确版本化的 Schema。Schema 版本与 Addition 自身版本、Data Schema 版本、Core API 版本分别管理，不得混为一谈。

Manifest 至少应通过自动校验：

- Addition ID、类型、版本和必需字段合法；
- ID 在 Registry 中唯一；
- 依赖、冲突和兼容性声明格式正确；
- 能力与权限声明具有宿主可识别的标识；
- 发布资产地址、大小、SHA-256 和签名信息完整且相互匹配；
- Data Addition 的 Schema 声明与实际数据包一致；
- Registry README 中的名称、类型、状态和版本与 Manifest 一致。

Core 必须拒绝无法解析、Schema 不兼容、身份冲突或关键字段缺失的 Manifest。不得因为 README 中存在某一条目就信任或执行对应包；包身份、签名、哈希、兼容性和权限仍须按照 ADR 0032 及相关安全决策验证。

### 6. 统一 Addition 登记生命周期

Addition Registry 使用以下生命周期状态：

| Status | 含义 | 使用条件 |
|---|---|---|
| `Proposed` | 候选 | 已提出，但尚未完成登记审核 |
| `Planned` | 已计划 | 已通过登记审核，尚未进入实质开发 |
| `In Development` | 开发中 | 已登记并正在实现或准备发布 |
| `Available` | 可获取 | 存在经过校验、可按兼容范围安装的正式发布资产 |
| `Deprecated` | 已弃用 | 不再推荐新用户安装，仍可能为既有用户提供有限维护或迁移期 |
| `Withdrawn` | 已撤回 | 已停止提供安装资产，或因安全、许可、维护等原因撤回 |

状态规则：

- `Available` 必须有可验证的发布资产和完整 Manifest；只有源码、页面占位或 ADR 不足以进入该状态。
- `Deprecated` 必须说明弃用原因、影响版本、迁移建议及维护期限（如适用）。
- `Withdrawn` 必须说明撤回原因、是否存在替代项，以及既有安装如何处理。
- 已撤回的 Addition 保留在 Registry 的历史区域，不得删除其身份记录。
- Registry 生命周期与本地安装状态（Installed、Enabled、Loaded 等）相互独立。
- Registry 生命周期也与 ADR 的 Decision / Implementation 状态相互独立。

### 7. 建立统一登记、审核和发布流程

新增官方 Addition 按以下顺序处理：

1. **提出**：描述目标用户、用途、Type、功能边界，以及为什么需要独立 Addition。
2. **架构审查**：检查 Core / Addition 边界、重复能力、API、依赖、权限、数据来源和隐私影响，确认遵循 ADR 0032。
3. **登记**：审核通过后分配正式 Addition ID，建立说明页与 Manifest，并将条目加入总 README。
4. **开发与验证**：按关联 ADR 和 Feature Spec 实现，校验 Manifest、依赖、兼容性和安全边界。
5. **发布**：发布包资产、校验信息与签名，更新 Manifest 和总 README；满足可安装条件后将状态设为 `Available`。
6. **维护**：版本更新、兼容性变化、权限变化、弃用或撤回应同步更新 Registry、Manifest 和发布资产。
7. **审计**：定期检查失效链接、资产缺失、Manifest 不一致、长期无人维护及安全撤回信息。

登记不等于发布。处于 Proposed、Planned 或 In Development 的项目必须明确显示不可安装，不得让用户误以为已经提供下载。

### 8. 与 ADR 0032 和 ADR 0053 的关系

ADR 0032 继续作为 Additions 的基础架构决策，负责定义原生入口、可选能力交付、Feature/Data 类型、Core 与 Addition 边界、受控 API、安装事务、签名校验和普通启动隔离。本 ADR 不取代这些架构决策。

本 ADR 接管 ADR 0032 第 6 节中“官方目录与登记信息如何组织、维护和治理”的具体规范，并进一步明确 Registry、Manifest 和登记流程。ADR 0032 第 6 节关于目录应包含的技术元数据继续有效；如有冲突，以本 ADR 对登记目录组织和治理流程的细化为准。ADR 0032 其余章节继续有效。

ADR 0053 负责定义能力声明、用户授权和运行时执行控制。Registry / Manifest 只记录扩展声明的能力和权限，不授予权限，也不替代 Core 的运行时检查。若 ADR 0053 后续被修改或拒绝，Manifest 的权限字段必须随最终 Accepted 决策调整；在此之前不得将 Proposed 内容写成已生效的架构要求。

### 9. 迁移既有扩展 ADR，但不抹除其架构价值

现有 ADR 中有些文档同时承担功能架构设计和扩展提案说明。迁移时应区分“作为扩展目录入口的职责”与“作为架构决策的内容”：

- 明确以独立 Addition 交付的项目，应建立 Registry 条目，并在原 ADR 顶部添加指向对应登记条目的链接。
- 原 ADR 不再作为官方扩展清单或发布状态来源；扩展名称、ID、版本、依赖、状态和下载信息以 Registry / Manifest 为准。
- 若原 ADR 的技术决策仍有效，不得仅因其最初为了某个 Addition 而创建，就将整份 ADR 标记为 `Superseded` 或 `Deprecated`。
- 只有当后续 ADR 真正取代原 ADR 的架构决策时，才按 ADR README 的规则标记 `Superseded`，并明确取代范围。
- 若原 ADR 只包含扩展产品提案且其决策整体被新方案放弃，才可在说明原因和迁移方向后标记为 `Deprecated` 或 `Rejected`。
- 已经被其他 ADR 取代的历史 ADR 保持原状态，不因本次目录整理而重新打开或改变关系。
- 本次迁移应优先审查明确声明独立 Addition 交付的 ADR；其他仅引用 ADR 0032 或具有可选数据依赖的领域 ADR，不得自动认定为 Addition。

本 ADR 提案阶段的迁移候选清单和最终范围，须在评审时逐项确认。初步候选包括 ADR 0033、0040、0041、0044 和 0049；ADR 0035 已由 ADR 0036 取代其主要交付方向，应保留现有历史状态。候选清单不代表这些 ADR 的架构决策将被废弃，也不代表其 Addition 已经正式注册。

### 10. 保持普通用户的使用路径不变

Registry 是供发现和管理扩展的入口，不得成为普通用户启动 ECL、创建实例或启动 Minecraft 的前置步骤。未安装任何 Addition 时，普通启动路径必须保持可用。

用户不需要理解 ADR、Manifest 或 Addition ID 才能使用扩展；这些是维护者和系统内部的治理信息。面向用户的页面应以功能名称、用途、兼容性、权限、状态和安装影响为主要信息。

## 备选方案

### 方案 A：继续以功能 ADR 作为扩展清单

优点是无需增加文档结构。缺点是目录分散，ADR 状态与扩展发布状态容易混淆，也无法为 Core 提供稳定、统一的机器可读登记入口。不采用。

### 方案 B：只维护一份机器可读 Registry 文件

优点是容易供 Core 和工具读取。缺点是用户难以浏览，功能说明、迁移信息和审核理由会被挤入机器字段，也不利于维护者审阅。不单独采用。

### 方案 C：以一个 README 统领 Registry，并为每个 Addition 配套说明页和 Manifest

该方案同时提供统一的人类阅读入口、单项说明和机器可读契约；与 ADR 0032 的现有仓库分发模式兼容，且不需要新仓库或自有服务端。作为本 ADR 提案方向。

## 后果

### 正面影响

- 用户和维护者有唯一入口发现官方 Additions。
- Addition 身份、功能说明、状态和发布信息不再散落在多个 ADR。
- Core 可以通过 Manifest 读取并校验扩展元数据。
- ADR 保持架构决策职责，不再承担产品目录和发布状态职责。
- 既有 ADR 的技术价值可以保留，同时清理过时的登记职责。
- 新扩展拥有明确的提出、审核、登记、发布、维护和撤回流程。

### 代价与风险

- 需要维护 Registry README、单项说明和 Manifest 三者的一致性。
- 需要定义并维护 Manifest Schema 及自动校验工具。
- 迁移既有 ADR 时必须逐项判断，不能批量修改状态。
- 若缺少发布自动化，手动更新容易造成 Registry 与 Release Assets 不一致。
- Addition 数量增长后，需要明确维护责任和定期审计机制。

## 验收标准

- 公有仓库存在唯一的 `docs/additions/README.md` 官方 Addition 总目录。
- 每个正式登记的 Addition 都有稳定唯一 ID、独立说明页和机器可读 Manifest。
- README 与 Manifest 的名称、类型、状态、版本等字段通过自动化一致性校验。
- Manifest Schema 有明确版本，并能拒绝缺失字段、重复 ID 和不兼容格式。
- 目录和发布资产遵循 ADR 0032，不新增专用仓库或强制自有服务端。
- Registry 状态与 ADR 状态、本地安装状态分别管理。
- 只有具备可验证发布资产的 Addition 才能标记为 Available。
- 新 Addition 的审核、登记、发布、弃用和撤回流程有明确记录。
- 既有扩展 ADR 已完成逐项审查；每个确认的 Addition 都能从总 README 找到，相关 ADR 也能反向链接对应登记项。
- 不因登记目录不可用、扩展未安装或 Manifest 读取失败而影响 ECL 普通启动和游戏启动。
- 不会仅因 ADR 曾为扩展而创建，就错误废弃仍然有效的领域架构决策。

## 修订记录

- 2026-11-07：维护者确认批准本决策；Implementation 保持 Not Started，批准不代表实现完成。完成首批 Addition Registry 登记时，按已批准规则建立单项 README 与 Manifest。

- 2026-11-04：首次提出统一 Addition Registry 与 Manifest 治理规范；明确以 `docs/additions/README.md` 作为唯一官方扩展目录，并提出既有扩展 ADR 的逐项迁移原则。Decision 保持 Proposed，等待评审。
