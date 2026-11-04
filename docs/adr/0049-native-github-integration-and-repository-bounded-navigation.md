# ADR 0049: Native GitHub Integration and Repository-Bounded Navigation

- Decision: Accepted
- Implementation: In Progress
- Date: 2026-11-03
- Scope: GitHub 项目中心 Feature Addition、ECL 原生页面入口、GitHub App 用户授权、仓库数据访问、Issue 反馈与仓库边界内导航
- Related: [ADR 0032：原生功能入口与按需 Additions 扩展体系](0032-native-feature-interfaces-and-optional-additions.md)、[ADR 0021：WPF 交互反馈与轻量动效系统](0021-wpf-interaction-and-motion-system.md)

> **Addition 登记说明：** 本 ADR 记录领域架构决策，不再作为扩展目录或发布状态的权威来源。相关 Addition 的正式身份、登记状态、版本与发布信息统一以 [ECL Additions Registry](../additions/README.md) 为准；当前登记目录中的对应项目仍属于候选，须完成 ADR 0054 评审与登记流程后才成为正式条目。

## 背景

ECL 希望在启动器内提供项目动态和社区协作入口，使用户无需离开 ECL 即可查看项目更新日志、Release、公开 Issues 和 Discussions，并使用 ECL 原生表单提交 Bug Report、Feature Request 或 Issue 评论。

该功能是一个通过 Additions 分发的 GitHub 项目中心 Feature Addition。ECL 随启动器提供原生功能入口、页面框架、未安装提示和 Addition 管理体验；GitHub 项目中心的具体领域能力由独立版本化的 Feature Addition 提供。所有页面、导航、表单、Markdown 内容和状态均由 ECL 原生 UI 组件渲染，不嵌入或打开 GitHub 网页。GitHub 仅作为远程数据与协作后端，通过 GitHub API 提供数据并接受用户明确发起的写入操作。

项目需要用户授权以用户身份提交 Issue 和评论，但不应因此获得用户其他仓库、代码、账户或组织的广泛访问能力。内容中的 GitHub 引用应解析为 ECL 内部的原生资源路由，且只能解析到当前配置的 ECL 公共仓库；不提供网页浏览或外部网页跳转能力。

## 决策

### 1. GitHub 项目中心作为 Feature Addition 分发，页面由 ECL 原生实现

GitHub 项目中心的功能实现作为独立 Feature Addition 分发，拥有独立版本、依赖声明、Core API 兼容范围、包校验与生命周期。它不属于 ECL Core 的内置领域功能，也不得成为普通启动链的依赖。

ECL 随启动器提供固定的原生功能入口和页面宿主。未安装 Addition 时，入口仍然存在，并展示功能说明、所需 Addition、安装状态和安装入口；安装后由原生页面通过受控 Core API 调用 Addition 提供的能力。Addition 不得动态注入任意导航项、页面或 WPF 控件。

GitHub Addition 提供版本化、受控的领域能力接口，至少包含：

- Release Provider：读取 Release、预发布版本和更新说明；
- Issue Provider：读取 Issue、评论和状态，并按允许的操作创建 Issue、发表评论；
- Discussion Provider：只读读取公开 Discussions、分类和回复；
- Native Resource Resolver：将 GitHub 引用解析为固定仓库内受支持的原生资源目标；
- Markdown Renderer：将 GitHub 返回的 Markdown 安全渲染为 ECL 原生内容；
- Authentication Manager：管理 GitHub App 用户授权及令牌生命周期；
- Cache / Rate Limit Manager：处理分页、缓存、ETag、速率限制和错误恢复。

原生 UI 宿主负责页面布局、列表、详情、筛选、Issue Composer、Comment Composer、确认和错误反馈。Addition 提供数据访问、授权流程适配、领域操作和原生资源解析能力；UI 不直接拼接任意 GitHub API 请求，也不持有 App 私钥或其他长期应用机密。

### 2. GitHub App 作为 ECL 的应用身份，使用用户授权令牌执行用户操作

ECL 注册为 GitHub App。用户从 ECL 发起连接流程，在 GitHub 完成授权后，ECL 获得代表该用户的 User Access Token，并以该令牌执行获准的 API 操作。

- 读取公开项目数据不强制用户登录；未连接时仍可浏览公开 Release、Issues、Discussions 等内容。
- 创建 Issue 和发表评论必须使用有效的用户授权令牌。
- API 操作同时受 GitHub App 权限和当前用户在目标仓库中的实际权限约束。
- 用户可在 ECL 中断开连接；令牌撤销、过期或失效时，退回匿名公开读取模式，并提示需要重新授权的写入功能。
- 令牌保存在本地安全存储中，不写入日志、崩溃报告、实例元数据或普通配置文件。
- ECL 不将用户令牌转发到自有服务端，不上传用户授权凭据。
- App 私钥、Client Secret 或其他服务端机密不得嵌入桌面客户端。授权实现必须选用适用于桌面客户端的 GitHub App 用户授权流程；如所选流程需要机密凭据，必须由受控的最小授权交换组件处理，且不得借此代理 GitHub API 或收集用户业务数据。

GitHub App 安装范围限制为 ECL 指定的公开仓库，不请求用户的全部仓库。目标仓库身份由 ECL 固定配置，不允许由页面内容、Issue 正文或外部链接动态改变。

### 3. GitHub App 权限采用最小权限清单

初始 App 权限固定为：

| 权限类别 | 权限 | 级别 | 用途 |
|---|---|---|---|
| Repository | Metadata | Read-only（GitHub 基础要求） | 仓库身份与基础元数据 |
| Repository | Issues | Read and write | 读取 Issues / 评论；创建 Issue；创建 Issue 评论 |
| Repository | Discussions | Read-only | 读取公开 Discussions、分类和回复 |

除此之外，初始版本不申请任何 Repository、Organization、Enterprise 或 Account 权限。尤其不申请：

- Contents、Pull requests、Actions、Workflows、Commit statuses、Checks；
- Administration、Webhooks、Repository hooks、Projects；
- Secrets、Environments、Packages、Deployments；
- Organization Members、Organization Administration、Account Email 等账户或组织权限。

公开 Release、更新说明、公开 Issue、Commit 和公开 Discussions 的读取应优先使用 GitHub 对代表用户的 GitHub App User Access Token 提供的公开资源读取能力，不为了公开读取而额外申请 Contents、Pull requests 或其他写权限。若具体 API 端点经验证确实要求额外权限，应先评估替代端点或匿名读取；只有经单独评审并修订本 ADR 后才可增加权限。

Discussions 明确为只读：第一阶段不创建 Discussion、不回复 Discussion、不编辑或删除 Discussion 内容。Issue 评论则在第一阶段开放创建能力。

GitHub App 的权限是平台授予的能力上限，不等于 ECL 必须暴露全部能力。Addition 的领域 API 必须进一步执行操作白名单与对象级权限约束：

- 允许：读取公开 Issue 与评论、创建新 Issue、创建 Issue 评论；Issue 作者可编辑和关闭自己创建的 Issue。
- 允许：仓库维护者依据其 GitHub 实际角色和 GitHub 服务端授权，执行相应 Issue 编辑、关闭及管理操作。
- 禁止：普通用户编辑或关闭他人创建的 Issue；普通用户管理标签、里程碑、指派人、锁定或置顶 Issue。
- 第一阶段不开放评论编辑或删除；不开放 Issue 删除。重新打开 Issue 的普通用户权限不在本 ADR 的初始范围内，必须以 GitHub 服务端实际授权为准。
- 允许：读取公开 Discussions、分类和回复。
- 禁止：创建、编辑、删除或回复 Discussion。
- 不提供任意 REST / GraphQL 请求接口，不把令牌暴露给 UI 或 Addition。

所有写操作必须绑定固定目标仓库。执行前由 Addition 校验仓库身份、操作类型、目标 Issue、当前授权身份和请求字段；对于作者编辑/关闭自己的 Issue，应核对当前用户与 Issue 作者身份。维护者操作能力以 GitHub 服务端授权结果为最终依据，不能仅依赖客户端缓存的角色信息或隐藏按钮作为安全边界。UI 展示完整提交内容并取得用户明确确认。对重复提交应使用请求状态和可识别的提交结果降低重复创建风险。

Issue 创建与编辑共用同一个原生 Issue Composer，编辑模式预填现有内容；评论使用独立的 Comment Composer。提交服务统一处理草稿、验证、授权失败、速率限制、重复提交和结果反馈。

### 4. 仓库范围固定，权限与资源访问边界相互独立

初始集成只面向 ECL 公共仓库 **hellokug2699/Ender-Craft-Launcher**。私有 Core 仓库 **hellokug2699/Ender-Craft-Launcher-Core** 不属于此集成的目标仓库，不得因用户授权而自动纳入。

必须区分：

- **GitHub 授权边界**：GitHub App 实际获得的 API 权限和用户可访问资源；
- **ECL 产品边界**：ECL UI 只展示和操作固定目标仓库；
- **原生资源边界**：ECL 只解析和展示固定目标仓库内明确支持的资源类型。

即使令牌在 GitHub 平台上具备更广的公开读取能力，ECL 也不得因此开放其他仓库的浏览入口。仓库身份必须使用规范化后的 owner 与 repository，并在 API 响应中再次核对；不能仅信任页面路径、链接文本或 API 返回的可变显示名称。

### 5. 将 GitHub 引用解析为原生资源路由

ECL 不加载 GitHub HTML 页面，也不使用 WebView 或浏览器控件。Markdown 中的 GitHub 链接、资源引用和页面内关联项由原生 UI 解析，并交给 Addition 提供的 Native Resource Resolver，转换为 ECL 内部的类型化资源路由，再通过 GitHub API 获取对应数据。

允许解析的资源仅限于固定仓库 **hellokug2699/Ender-Craft-Launcher** 下已实现原生详情视图的资源类型，例如：

- Issues 及其评论锚点；
- Pull Requests；
- Commits、Branches、Tags；
- 仓库文件、目录及历史记录；
- Releases；
- Discussions 及其分类和主题。

解析结果必须是明确的资源类型和资源标识（例如 Issue 编号、Commit SHA、Release 标识或仓库内文件路径），并在调用 API 前核对固定仓库身份。不得将 Markdown 中的 URL 直接作为网络请求目标，也不得使用字符串包含或前缀匹配判断仓库归属。

以下引用不映射为可导航的原生资源：

- 其他 GitHub 仓库，包括同一 owner 下的其他仓库；
- GitHub 组织、用户、搜索等非目标仓库资源；
- GitHub 之外的网站、非资源链接及无法识别的引用；
- 不支持的资源类型或无法确认归属的资源。

无法解析的引用以不可点击文本呈现，或显示 ECL 原生的“不支持的仓库资源”提示；不尝试打开网页，也不提供临时放行入口。

相对路径和 Markdown 引用必须结合当前资源上下文解析，并在规范化后确认其仓库归属。路径穿越、编码歧义、重复分隔符或无法唯一解析的引用必须拒绝映射。

### 6. 所有原生资源入口必须执行统一解析

资源解析不能只绑定某个页面控件的点击事件。以下来源必须经过同一解析器：

- Markdown 正文、评论、Release Notes；
- Issue、PR、Commit、文件和 Discussion 页面中的链接；
- ECL 原生导航控件及历史记录；
- 用户头像、标签、引用和其他可点击关联内容；
- API 返回的 html_url、url 等链接字段；
- 任何未来增加的富文本或预览组件。

ECL 不跟随 Markdown 或 API 内容提供的任意网页重定向。API 请求目标由 Addition 根据固定仓库身份和受控 endpoint 构造；若 API 响应提供关联资源标识或链接，必须先解析并确认其资源类型与仓库身份，再交给原生路由。

所有 GitHub API 请求只能发往固定的 GitHub API 主机和由 Addition/Core 受控构造的目标仓库 endpoint；不得将 Markdown 链接、API 返回的任意 URL 或用户输入直接作为请求目标。分页使用受控的 endpoint 与参数构造，禁止跟随到其他仓库或外部主机。

### 7. GitHub 内容按不可信输入处理

Issue、评论、Discussion、Release Notes、文件内容和 Markdown 均视为不可信远程输入。ECL 使用自己的 Markdown 渲染器和原生控件，不执行远程 HTML 或脚本。

- 禁止脚本、任意 HTML、iframe、对象嵌入和远程 UI 注入。
- GitHub 资源引用必须交由 Native Resource Resolver 解析。
- 默认不加载远程图片，避免第三方图片服务器获得用户连接元数据；如未来支持图片，必须单独设计来源策略和明确的隐私说明。
- 不自动执行代码块、命令、附件或下载内容。
- 对异常长文本、嵌套结构、图片尺寸和解析耗时设置资源上限。
- Markdown 解析失败时以安全纯文本回退，不允许因此跳过链接校验。

### 8. Bug Report 与 Issue 提交遵循本地优先和明确披露

ECL 提供原生 Bug Report / Feature Request 表单和项目定义的模板。表单可支持标题、问题描述、复现步骤、预期行为、实际行为、环境版本等结构化字段。

- 用户填写内容和自动收集的候选诊断信息必须分开呈现。
- 日志、崩溃报告、实例路径、服务器地址、配置和其他本地数据默认不附加、不上传。
- 如提供自动诊断信息建议，必须逐项列出来源、内容和潜在敏感性，默认不勾选。
- 提交前显示完整 Markdown 预览、目标仓库、提交类型和附件清单。
- 用户明确确认后才发送 API 请求。
- 不因 GitHub 登录或授权而视为用户同意上传本地数据。
- 提交失败时保留本地草稿，并清楚区分网络失败、权限不足、速率限制、验证错误和仓库拒绝。
- 成功后展示 Issue 编号和仓库内链接；链接仍通过仓库导航策略处理。

ECL 不收集或上传遥测、使用统计、设备标识或与本次提交无关的用户信息。GitHub 作为第三方服务会接收正常 API 请求所需的连接元数据；这不等于 ECL 将用户数据上传到自有服务。

### 9. 缓存、速率限制和可用性

公开数据优先匿名读取；需要授权的写入使用 User Access Token。缓存按仓库、资源类型、资源 ID、分页参数和内容版本隔离，支持 ETag / 条件请求（端点支持时）、分页和明确的过期状态。

- 缓存不得混淆不同仓库或不同用户的私有可见数据。
- 公开数据缓存可以在断网时展示，并明确标记最后更新时间。
- 令牌失效、权限不足、速率限制和服务不可用应分别处理。
- 不通过轮询制造无必要的 API 请求；优先按需刷新。
- GitHub 不可用不得影响 ECL 启动、实例管理或 Minecraft 启动。
- 不建设 ECL 自有 GitHub API 代理、内容镜像或强制在线服务。

### 10. 与现有架构的边界

- ADR 0032：GitHub 项目中心作为 Feature Addition 分发；ECL 原生提供固定入口、页面宿主、Composer 和状态呈现，Addition 提供可选领域能力。未安装 Addition 不影响普通启动。
- Core 只提供扩展加载、受控 API、网络基础设施、安全存储等通用能力；GitHub 仓库规则、Issue 操作和 GitHub API 领域逻辑属于该 Addition，不得把 GitHub 专属业务逻辑塞入 Core。
- ADR 0021：Issue 创建、编辑、关闭、评论等写操作使用统一操作确认与结果反馈原则。
- 本 ADR 不定义通用 GitHub 客户端、不支持任意仓库切换、不支持代码编辑、Git 操作、PR 创建/合并、Actions 管理或仓库设置管理。

## 备选方案

### 方案 A：将 GitHub 项目中心作为 Core 内置功能

不采用。该功能属于可选的项目协作能力，应通过 Feature Addition 独立分发和更新，避免扩大 Core 的内置功能与依赖面；ECL 仍提供原生入口和未安装状态。

### 方案 B：传统 OAuth App

不采用。OAuth App 的权限粒度较粗，难以将 Issues 写入与代码、仓库内容及其他能力分开授权。GitHub App 的细粒度 Repository Permissions 更符合最小权限原则。

### 方案 C：GitHub App 请求广泛读取和写入权限

不采用。读取公开项目动态不构成申请 Contents、Pull requests、Actions 或账户权限的理由。初始权限限定为 Metadata Read、Issues Read/Write、Discussions Read。

### 方案 D：ECL 自建代理统一访问 GitHub

不采用。代理会引入服务端凭据、用户数据处理、可用性和运维责任，与本地优先原则冲突。桌面端直接使用 GitHub API，并在本地安全保存用户令牌。

### 方案 E：由用户提供 Personal Access Token

不采用。要求用户自行创建和管理 PAT 会增加操作负担，也容易诱导用户授予过宽权限。ECL 使用自己的 GitHub App 身份完成授权。

## 后果

### 正面影响

- 用户无需离开 ECL 即可查看项目动态和参与 Issue 反馈。
- 页面由 ECL 原生渲染，交互、导航和安全边界由项目控制。
- GitHub App 的细粒度权限比传统 OAuth App 更符合最小权限原则。
- 公开读取不依赖用户登录；只有写入操作需要授权。
- 用户授权范围不包含代码、仓库管理、组织或账户管理权限。
- 通过固定仓库身份和统一原生资源解析，远程内容无法让 ECL 访问其他仓库或任意网络目标。
- 本地优先的反馈流程让用户在提交前掌握完整内容。

### 代价与限制

- GitHub App 用户授权、令牌刷新/撤销和本地安全存储需要专门实现。
- Issues: Write 是资源级权限，不等于仅允许创建 Issue；ECL 必须通过 Core API 操作白名单进一步限制能力。
- Discussions 读取与 Issue 评论使用不同的 API 能力和权限，需分别验证 REST / GraphQL 端点的实际权限要求。
- GitHub API 速率限制、分页和服务可用性会影响数据刷新。
- 严格仓库边界意味着 ECL 不会将外部引用或其他仓库引用转换为可导航资源。
- GitHub App 权限变更可能要求安装者重新批准；增加权限必须重新评审并说明原因。

## 验收标准

- 未连接 GitHub 时，用户仍可查看目标公开仓库的 Release、Issues 和 Discussions。
- GitHub App 安装范围只包含 hellokug2699/Ender-Craft-Launcher，不请求用户全部仓库。
- 初始权限严格为 Metadata Read-only、Issues Read and write、Discussions Read-only；其他 Repository、Organization、Enterprise 和 Account 权限均为 None。
- App 不包含客户端可提取的私钥或长期 Client Secret。
- 用户授权令牌保存在本地安全存储，不出现在日志、普通配置或诊断附件中。
- 用户令牌失效或撤销后，公开读取继续可用，写入功能明确提示重新授权。
- 能读取目标仓库公开 Release、Issue、Issue 评论和 Discussion 内容。
- 能创建 Issue 和 Issue 评论；第一阶段不能创建或回复 Discussion。
- 普通用户可以编辑和关闭自己创建的 Issue，但不能编辑或关闭他人的 Issue；仓库维护者的操作范围由 GitHub 服务端角色权限决定。
- 普通用户不能管理标签、里程碑、指派人、锁定或置顶 Issue；第一阶段不能删除 Issue 或编辑/删除评论。
- Issue 创建与编辑共用原生 Issue Composer，评论使用 Comment Composer；Markdown 内容使用统一原生渲染器和统一仓库内导航策略。
- GitHub 项目中心的领域实现作为独立 Feature Addition 分发；未安装时原生入口仍显示说明和安装提示，安装/更新失败不影响普通启动。
- Issue 提交前显示完整内容和目标仓库，并要求用户明确确认。
- 不自动附加日志、存档、实例文件、设备标识或其他本地数据。
- 原生资源解析仅允许目标仓库内已实现原生视图的 Issue、PR、Commit、Branch、Tag、文件、目录、Release 和 Discussion 资源。
- 其他仓库、非目标仓库资源、外部网站和不支持的资源引用不会被解析为可导航目标；ECL 不打开网页或跟随内容提供的任意重定向。
- Markdown 相对引用、路径规范化、编码歧义、路径穿越和资源归属测试不能绕过固定仓库边界。
- 所有 Markdown、评论、Release Notes 和 API 返回的资源引用均经过统一原生资源解析器。
- API endpoint 和分页链接只允许固定 GitHub API 主机及目标仓库路径。
- Markdown 不执行脚本或任意 HTML，默认不加载远程图片。
- 缓存隔离、分页、速率限制和离线状态均有明确处理。
- GitHub 服务不可用不会影响 ECL 普通启动、实例管理和 Minecraft 启动。

## 修订记录

- 2026-11-03：首次提出 ECL 原生 GitHub 项目中心；确定所有内容由 ECL 原生渲染、GitHub 仅提供数据；确定 GitHub App 用户授权、最小权限、固定仓库边界、原生资源解析和本地优先的 Issue 反馈流程。
- 2026-11-03：根据维护者讨论修正 Issue 权限模型：作者可编辑和关闭自己的 Issue，普通用户不能操作他人的 Issue，维护者按 GitHub 服务端实际角色授权；评论编辑/删除、Issue 删除及普通用户仓库管理操作仍不开放。
- 2026-11-03：明确 GitHub 项目中心作为 Feature Addition 独立分发；ECL 原生提供入口、页面宿主与未安装提示，Addition 提供 GitHub 领域能力。明确不嵌入网页，所有页面与内容均由 ECL 原生组件渲染，GitHub 引用转换为受控的原生资源路由。
- 2026-11-03：维护者指示实施后进入 `Accepted + In Progress`。已实现固定仓库边界、统一原生资源解析器、Markdown 链接解析、受控 API endpoint、匿名公开读取/写入授权边界、ETag 缓存与速率限制错误分类；GitHub App 用户授权、平台安全令牌存储、Discussions 匿名读取、Additions 打包与原生页面仍未交付，故未标记为 `Implemented`。证据见 [ADR 0041–0049 实施记录](../adr-0041-0049-implementation.md)。
