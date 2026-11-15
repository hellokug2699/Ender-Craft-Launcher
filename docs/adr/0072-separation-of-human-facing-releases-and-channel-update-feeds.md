# ADR 0072: Separation of Human-Facing Releases and Channel Update Feeds

- Decision: Accepted
- Implementation: In Progress
- Date: 2026-11-15
- Scope: ECL Windows x64 / ARM64 官方 Release 页面、频道更新 Feed 与自动更新发布链路
- Related: [ADR 0013: Branch-Based Versioning and Release Channels](0013-branch-based-versioning-and-release-channels.md)、[ADR 0051: Unified Installation, Update and Recovery Architecture](0051-unified-installation-update-and-recovery-architecture.md)、[ADR 0071: Self-Contained Installer Executable and Embedded Deployment Media](0071-self-contained-installer-and-embedded-deployment-media.md)

## 背景

ECL 目前把面向用户的下载入口与客户端自动更新源放在同一组 GitHub Release 资产中。Release 页面因此同时暴露 Installer、Bootstrapper、ECL 完整程序包、Manifest、签名和信任材料。普通用户很难判断应该下载什么；即使 Release Notes 已经说明用途，下载列表仍然呈现为多个并列文件。

这两类分发具有不同的消费者和生命周期：

- 人工下载面向用户，提供首次安装入口与必要时的独立修复入口。
- 自动更新面向 ECL / Bootstrapper，必须取得签名 Manifest、签名文件和 Manifest 指定的完整 ECL Package。
- Installer 是首次安装与完整离线恢复介质，不是日常更新包；Bootstrapper 是部署执行器，也不是日常更新包。

GitHub Release 的附件都会显示在 Release 页面中，无法将部分附件作为“隐藏的更新源资产”。因此，若要求 Release 页面只展示两个 EXE，就必须把频道更新 Feed 从 GitHub Release 资产中分离，而不能简单删除更新协议所需文件。

## 决策

### 1. 人工 Release 只提供两个可执行文件

从本决策切换完成后创建或更新的面向用户 Release（包括频道滚动 Release 与新的不可变版本 Release）只提供以下两个下载资产：

1. `ECL.Installer-<version>-win-x64.exe`：普通用户的唯一推荐下载入口，负责首次安装和完整离线恢复。
2. `ECL.Bootstrapper-<version>-win-x64.exe`：高级用户在需要独立修复入口时使用；普通用户无需单独下载。

ARM64 构建按相同命名规则提供对应架构的两个 EXE。Release 页面不得附带 Installer ZIP、Bootstrapper ZIP、ECL Package、Manifest、signature、trust store、checksum sidecar 或其他构建中间产物。

Release Notes 必须明确提示普通用户只下载 Installer；只有在需要独立修复入口时才下载 Bootstrapper。切换前的历史 ZIP-only Release 保留为历史记录：旧 Installer ZIP 可能依赖外置介质，不能仅改名为 EXE 伪装成新的自包含 Installer；只有同一历史 Release 中已经存在对应版本的 Installer EXE 与 Bootstrapper EXE 时，自动迁移步骤才可清理其余附件并补齐摘要；不得从旧 ZIP 解压或重命名生成所谓的独立 EXE，因为无法据此证明其为自包含构建。Release Notes 同时列出两个 EXE 的 SHA-256，作为可从官方 Release 页面独立获取的摘要。摘要不作为第三个下载资产。

### 2. 自动更新继续使用 ECL Package

日常更新不下载 Installer，也不下载独立 Bootstrapper。客户端仍然：

1. 读取当前频道的 `update-manifest.sig` 与 `update-manifest.json`；
2. 使用客户端已持有的更新信任根验证 Manifest 签名；
3. 校验 Manifest 的产品、频道、版本、平台、架构和兼容性；
4. 下载 Manifest 指定的完整 ECL Package ZIP；
5. 校验包大小、SHA-256 和逐文件清单；
6. 将已验证的 Package 交给 Bootstrapper 执行 staging、提交和回滚。

Bootstrapper 自身的更新属于独立生命周期，不得与 ECL Package 更新混为一谈。其恢复来源至少应保留一个独立、可验证的 Bootstrapper EXE；Installer 内嵌的 Bootstrapper 仍是完整安装/恢复介质的一部分。

### 3. 频道更新 Feed 与人工 Release 分离

官方频道 Feed 使用 **Cloudflare Pages（Direct Upload）** 提供静态公开 HTTPS 元数据；完整 ECL Package 存放在同一公有仓库的三个机器专用 GitHub Releases 中，不使用普通人工版本 Release 附件。Pages 不运行 Pages Functions、Workers 或其他动态 API，也不需要 R2。其逻辑目录结构保持稳定：

```text
# Cloudflare Pages：仅频道元数据
https://updates.endercraftlauncher.top/
├─ main/update-manifest.json
├─ main/update-manifest.sig
├─ dev/update-manifest.json
├─ dev/update-manifest.sig
├─ nightly/update-manifest.json
└─ nightly/update-manifest.sig

# 同一公有仓库的机器专用 GitHub Releases：只追加不可变 Package assets
main-packages     # main 频道
dev-packages      # dev 频道
nightly-packages  # nightly 频道

# 每个 Release 只追加版本号 + SHA-256 唯一命名的 ZIP，不覆盖、不删除
# Manifest 指向对应频道 Release 的 asset 下载 URL：
# https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/<channel>-packages/<package>
```

- Feed 必须支持匿名读取，客户端不得依赖 GitHub 登录态、访问令牌或其他用户凭据。
- Feed 只负责传输，不授予内容信任。Manifest 签名、包哈希和逐文件校验仍是唯一允许进入部署的信任依据。
- `update-trust.json` 是客户端/安装介质持有的信任材料，不从 Feed 下载后直接建立信任；Feed 不得替换或轮换客户端的信任根。
- 机器专用 Package Release是仅追加的发布对象存储，不承载源代码、Manifest 或签名文件。Package 按频道分 Release，文件名包含平台架构、版本与 SHA-256，文件名包含 SHA-256；Manifest 中的 URL 必须固定到包含该 Package 的 Git commit SHA，而不是可移动的 机器专用 Package Release名，以确保 URL 不可变。
- `main`、`dev`、`nightly` 三个频道使用完全独立的 Package 命名空间和 Manifest 指针。main 仅由正式版本 Tag 推进，dev 仅由 `dev` 分支的受认可发布触发，nightly 仅由 `nightly` 分支的成功构建推进。任何频道都不得引用其他频道 Release中的 Package。
- 已经被任一已发布 Manifest 引用的 Package 永久保留，不得覆盖或删除；失败构建及尚未被任何已发布 Manifest 引用的临时 Package 不提交到 机器专用 Package Release。回滚只回退频道 Manifest/Signature，不删除 Package。
- 机器专用 Package Release写入必须采用 append-only 方式，禁止 force-push、历史重写和对已存在路径的覆盖。由于三个频道共享同一分支，写入采用 Git tree/blob plumbing，仅获取必要的对象，避免每次构建检出全部历史 Package；写入具备并发冲突重试与 stale-build 防护。Cloudflare Pages Production 部署由独立 `feed-lock` 分支上的 compare-and-swap 租约锁串行化，避免不同频道的完整站点部署互相覆盖。一次发布不得丢失其他频道已经提交的文件或指针。
- 频道 Manifest 与其 detached signature 必须作为同一逻辑版本原子切换。Pages 的每次部署是完整静态站点版本；发布流水线必须将所有频道元数据作为一个完整目录一次性部署到 Production，依赖 Pages 部署版本切换保证客户端只看到旧部署或新部署，不得分别上传/覆盖两个文件。不得把 Preview Deployment 当作生产 Feed。
- 新版本发布顺序为：构建并签名 Package/Manifest → 将 Package 以新路径追加到 机器专用 Package Release并取得该次 commit SHA → 使用该 commit SHA 生成 Package 绝对 HTTPS URL 并重新签名 Manifest → 从 `github.com` Release asset URL 匿名下载并验证 Package 字节数与 SHA-256 → 生成包含三个频道当前 Manifest/Signature 的完整 Pages 静态目录 → 使用 Wrangler Direct Upload 部署 Production → 从正式自定义域名验证 Manifest、Signature、Package 下载与签名/哈希。不得在 Package asset 上传成功、可匿名下载且哈希验证通过之前推进对应频道 Feed。只有全部步骤成功后，构建才算 Feed 发布成功。
- 回滚通过恢复上一份已签名的频道 Manifest/Signature 指针完成；不得删除仍被旧 Manifest 引用的 Package。

Pages 项目采用 Direct Upload 创建，禁止绑定 Git Integration；项目名建议为 `ender-craft-launcher-feed`，正式地址为 `https://updates.endercraftlauncher.top/`。该子域名可以由现有 DNS 服务商托管：先在 Pages 项目的 Custom domains 中登记，再在域名 DNS 服务商添加指向 `ender-craft-launcher-feed.pages.dev` 的 CNAME；无需将整个根域名迁移到 Cloudflare。GitHub Actions 使用仅具备目标 Account 下 Cloudflare Pages Edit 权限的 API Token，通过 Wrangler 部署预先生成的静态目录。Token 与 Account ID 只存放于 GitHub Actions Secrets。Pages Free 的文件数、单文件大小和构建配额必须在部署前核对；ECL Feed 只放小体积元数据，Package 不得上传 Pages。GitHub Actions Artifact 仅用于短期构建追溯，不得作为客户端 Feed。

### 4. 发布记录与频道更新状态相互独立

- GitHub Release 负责人工下载、版本说明和两个 EXE 的摘要展示；完整 ECL Package 不得作为 Release 附件。
- Channel Feed 负责自动更新所需的 Manifest、Signature 和 ECL Package。
- Actions Artifact 负责短期构建追溯，不作为人工长期下载入口，也不作为客户端更新源。
- Release tag 与 Feed 的频道指针可以共享同一构建版本元数据，但两者不是同一个存储对象；不得因为 Release 创建成功就认定 Feed 发布成功。
- 发布流水线必须分别报告 Release 发布状态与 Feed 发布状态。Feed 发布失败时不得将该版本标记为频道最新版本；Release 可保留供人工下载，但必须明确标注自动更新 Feed 尚未完成。

### 5. 迁移与兼容

现有客户端默认 Feed 仍指向 GitHub Release 下载路径。迁移必须分阶段完成：

1. 先部署独立 Feed，并使用现有 Manifest、Signature、Package 完成匿名下载、验签、哈希和 Bootstrapper 部署测试。
2. 更新官方客户端默认 Feed 地址，或通过受控配置切换至新 Feed；保留显式 `ECL_UPDATE_FEED` 覆盖能力。
3. 验证 `main`、`dev`、`nightly` 三个频道的首次更新、重复更新、失败重试和回滚。
4. 确认受支持客户端不再依赖 GitHub Release 资产读取更新 Feed 后，才从人工 Release 中移除 Package、Manifest、Signature、trust store 等旧资产。
5. 迁移期间旧 Feed 资产必须继续保留；不得先清理旧资产再部署新 Feed。回滚时恢复旧 Feed 配置与对应资产。

## 备选方案

- **继续把所有文件放在 GitHub Release：**实现简单且当前客户端兼容，但 Release 页面持续暴露所有内部更新文件，无法满足只提供两个 EXE 的目标，因此不采用。
- **使用同一仓库的 机器专用 Package Release：**可以匿名提供静态文件、无需新增仓库或额外对象存储服务，并可用 commit SHA 固定不可变 URL；代价是仓库历史与体积持续增长，且需要 append-only 写入、并发冲突处理和存储容量监控。考虑到当前 Package 分发规模与避免新增仓库/付费存储的约束，采用此方案。
- **将更新文件改为 GitHub Actions Artifact：**Artifact 有保留期限，且客户端下载可能需要 GitHub 权限，不适合作为长期匿名更新源，因此不采用。
- **GitHub Pages + GitHub Releases：**可实现相同职责分离，但Cloudflare Pages 支持将子域名通过 CNAME 指向 Pages 项目；域名 DNS 可以继续由现有服务商托管，因此无需为了 Feed 迁移整个 Zone。
- **Cloudflare R2：**适合托管大型静态对象，但当前不作为前置依赖；Feed 元数据体积小，Pages 已足够，避免 R2 开通可能涉及的支付验证障碍。
- **让 Installer 或 Bootstrapper 直接承担日常 ECL 更新下载：**会混淆首次安装介质、部署执行器与更新内容的职责；日常更新仍应由 Manifest 指向 ECL Package。

## 后果

- Release 页面从多个并列文件收敛为 Installer 与 Bootstrapper 两个 EXE，普通用户的下载决策更明确。
- 自动更新仍然使用签名 ECL Package，不会因为人工下载入口变化而改变部署协议。
- 发布系统需要同时维护人工 Release 与独立 Feed 两条发布结果，并为 Feed 托管增加持久性、带宽、缓存与可用性监控。
- 频道 Feed 成为自动更新的关键基础设施；Feed 故障不得降低签名验证要求，也不得阻止用户使用 Installer 完整离线恢复。
- Pages 的 Production Deployment 提供整站版本回滚；回滚必须恢复一套完整、此前验证过的频道元数据，而不是手工替换单个文件。Feed 对象通过 `_headers` 设置 `Cache-Control: no-store`，客户端请求同时要求重新验证缓存，并在两次独立读取跨越部署切换时最多重试一次签名文件与 Manifest 的整对读取。
- Package 与 Feed 元数据由 GitHub 仓库与 Cloudflare Pages 分别提供，发布流水线必须先确认 机器专用 Package Release中的 commit-pinned Package 可匿名访问，再发布 Pages 元数据，避免出现已发布 Manifest 指向尚不可下载的 Package。
- 机器专用 Package Release会随历史 Package 永久保留而增长；必须监控仓库体积与 GitHub 单文件限制。不得把该分支作为常规开发分支检出或打包进客户端。若未来 Package 体积或累计仓库体积超出 GitHub 适用限制，应另行提出存储迁移 ADR，不得静默删除历史包。
- Manifest 的 `package.file` 继续兼容旧版相对路径，同时允许严格限定的 GitHub Release Assets 绝对 HTTPS URL。客户端校验初始主机、仓库、频道专用 Release tag、平台、架构、版本、文件名与 SHA-256；下载时逐跳校验 HTTPS 重定向，只允许 GitHub Release asset 分发主机；更新专用 HTTP 客户端关闭自动重定向，拒绝任何改变最终请求 URI 的响应。
- 在 Feed 供应商与端点尚未完成验证前，旧 Release Feed 必须保持可用；因此本 ADR 的目标状态不能通过单纯删减 Release 附件提前宣称完成。

## 验收标准

1. 切换后创建或更新的官方 Release 页面恰好包含两个可下载资产：对应架构的 Installer EXE 与 Bootstrapper EXE；不存在 ZIP、Manifest、Signature、trust store 或 checksum sidecar 附件。切换前仅有 ZIP Installer 的历史 Release 保留原样，不得通过重命名制造虚假的自包含 EXE。
2. Release Notes 明确说明 Installer 是普通用户入口、Bootstrapper 仅用于必要的独立修复，并分别列出与最终 EXE 字节一致的 SHA-256。
3. ECL 自动更新仍下载 Manifest 指定的完整 ECL Package，而不是 Installer 或 Bootstrapper。
4. `main`、`dev`、`nightly` 的 Feed 均可通过 `https://updates.endercraftlauncher.top/<channel>/` 匿名 HTTPS 读取 Manifest 与 Signature；Manifest 指向 机器专用 Package Release中由 commit SHA 固定的不可变 Package。三个频道的 Package 路径严格隔离。
5. Manifest 签名、包大小、SHA-256、逐文件哈希和频道/架构约束均通过客户端验证；篡改任一项必须 fail-closed。
6. Pages 每次 Production 部署必须原子切换完整静态站点，并为频道元数据设置 `Cache-Control: no-store`。由于客户端读取 Manifest 与 Signature 是两个独立请求，若部署切换恰好发生在两次请求之间，验签必须拒绝混配并自动重新读取整对文件一次；不得把未通过验签的 Manifest 交给部署器。
7. 新 Package 上传失败、签名失败或频道指针切换失败时，不得将频道推进到新版本；旧版本仍可下载和部署。
8. 回滚可恢复上一份已签名 Manifest/Signature，且不会删除仍被有效 Manifest 引用的 Package。
9. Actions Artifact 过期或不可访问，不影响客户端正常更新。
10. 旧 Release Feed 资产只能在新 Feed 端到端验证成功且确认没有已分发客户端继续依赖旧 Feed 后移除；若已有用户/已分发客户端，必须保留兼容过渡窗口或旧 Feed。当前 ECL 尚无用户或已分发客户端，因此首次迁移可在新 Feed 验证通过后清理当前滚动 Release 的旧 Feed 附件；ZIP-only 历史版本仍保留。
11. Feed 发布失败与 GitHub Release 发布成功必须被分别报告；不得把两者合并为单一成功状态。
12. 机器专用 Package Release写入只追加、不覆盖、不 force-push；并发发布不会丢失任一频道的 Package 或 Manifest 指针，过期构建不得回退较新频道版本。
13. 任一已发布 Manifest 引用过的 Package 永久保留；失败构建不进入 机器专用 Package Release。

## 修订记录

- 2026-11-15：首次决策。将人工 Release 与自动更新 Feed 分离；人工 Release 仅保留 Installer / Bootstrapper 两个 EXE，日常更新继续使用签名 ECL Package。
- 2026-11-15：曾决定采用 Cloudflare Pages Direct Upload 托管频道 Manifest/Signature、GitHub Releases 托管不可变 Package；该 Package 托管选择随后由本记录的最新修订取代，保留此条用于追溯决策演进。
- 2026-11-15：根据三频道独立更新与 Release 页面仅保留两个 EXE 的约束，改为由同一公有仓库的 机器专用 Package Release以 append-only 方式托管不可变 Package；Manifest 的 Package URL 固定到频道专用 Release tag 与 SHA-256 唯一资产名。三个频道独立命名空间、独立推进，已发布 Package 永久保留。
- 2026-11-15：开始实施绝对 Package URL 解析、频道绑定、专用无重定向 HTTP 传输、官方 Feed 默认地址、GitHub Packages 分支写入、三频道迁移与 Cloudflare Pages Direct Upload。Production Feed 使用独立租约锁串行化；端到端生产部署与 GitHub CI 尚待验证。
- 2026-11-15：明确切换前仅含 ZIP Installer 的历史 Release 不做不安全的 EXE 改名，严格的双 EXE 规则适用于切换后的新建/更新 Release；历史记录只在同一 Release 已同时存在对应版本的 Installer 与 Bootstrapper EXE 时才执行资产规范化，禁止从 ZIP 解压生成或伪装自包含 EXE。
- 2026-11-15：补充 Feed 元数据 `Cache-Control: no-store`，并在 Manifest/Signature 两次独立读取遇到部署切换导致验签失败时，客户端重新读取整对文件一次；持续验签失败仍 fail-closed。
