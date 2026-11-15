# ADR 0072: Separation of Human-Facing Releases and Channel Update Feeds

- Decision: Accepted
- Implementation: Not Started
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

每个面向用户的官方 Release（包括频道滚动 Release 与不可变版本 Release）只提供以下两个下载资产：

1. `ECL.Installer-<version>-win-x64.exe`：普通用户的唯一推荐下载入口，负责首次安装和完整离线恢复。
2. `ECL.Bootstrapper-<version>-win-x64.exe`：高级用户在需要独立修复入口时使用；普通用户无需单独下载。

ARM64 构建按相同命名规则提供对应架构的两个 EXE。Release 页面不得附带 Installer ZIP、Bootstrapper ZIP、ECL Package、Manifest、signature、trust store、checksum sidecar 或其他构建中间产物。

Release Notes 必须明确提示普通用户只下载 Installer；只有在需要独立修复入口时才下载 Bootstrapper。Release Notes 同时列出两个 EXE 的 SHA-256，作为可从官方 Release 页面独立获取的摘要。摘要不作为第三个下载资产。

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

官方频道 Feed 使用 **Cloudflare Pages（Direct Upload）** 提供静态公开 HTTPS 元数据；完整 ECL Package 继续由 GitHub Releases 托管。Pages 不运行 Pages Functions、Workers 或其他动态 API，也不需要 R2。其逻辑目录结构保持稳定：

```text
https://ender-craft-launcher-feed.pages.dev/
├─ main/
│  ├─ update-manifest.json
│  └─ update-manifest.sig
├─ dev/
│  ├─ update-manifest.json
│  └─ update-manifest.sig
└─ nightly/
   ├─ update-manifest.json
   └─ update-manifest.sig

# 完整 Package 由 Manifest 指向 GitHub Releases 中的不可变资产：
# https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/<tag>/<package>
```

- Feed 必须支持匿名读取，客户端不得依赖 GitHub 登录态、访问令牌或其他用户凭据。
- Feed 只负责传输，不授予内容信任。Manifest 签名、包哈希和逐文件校验仍是唯一允许进入部署的信任依据。
- `update-trust.json` 是客户端/安装介质持有的信任材料，不从 Feed 下载后直接建立信任；Feed 不得替换或轮换客户端的信任根。
- Package 使用版本化、不可变文件名。已发布版本的 Package 不得原位覆盖。
- 频道 Manifest 与其 detached signature 必须作为同一逻辑版本原子切换。Pages 的每次部署是完整静态站点版本；发布流水线必须将所有频道元数据作为一个完整目录一次性部署到 Production，依赖 Pages 部署版本切换保证客户端只看到旧部署或新部署，不得分别上传/覆盖两个文件。不得把 Preview Deployment 当作生产 Feed。
- 新版本发布顺序为：构建并签名 Package/Manifest → 创建带不可变版本标签的 GitHub Release 并上传 Package → 验证 Release Package 可匿名下载且其字节与 Manifest 哈希一致 → 生成包含所有频道当前元数据的 Pages 静态目录 → 使用 Wrangler Direct Upload 部署 Production → 从正式自定义域名验证 Manifest、Signature、Package 下载与签名/哈希。只有全部步骤成功后，构建才算 Feed 发布成功。
- 回滚通过恢复上一份已签名的频道 Manifest/Signature 指针完成；不得删除仍被旧 Manifest 引用的 Package。

Pages 项目采用 Direct Upload 创建，禁止绑定 Git Integration；项目名建议为 `ender-craft-launcher-feed`，使用 Cloudflare 自动分配的 `ender-craft-launcher-feed.pages.dev` 地址；不要求购买或绑定自定义域名，也不要求将现有域名接入 Cloudflare Zone。GitHub Actions 使用仅具备目标 Account 下 Cloudflare Pages Edit 权限的 API Token，通过 Wrangler 部署预先生成的静态目录。Token 与 Account ID 只存放于 GitHub Actions Secrets。Pages Free 的文件数、单文件大小和构建配额必须在部署前核对；ECL Feed 只放小体积元数据，Package 不得上传 Pages。GitHub Actions Artifact 仅用于短期构建追溯，不得作为客户端 Feed。

### 4. 发布记录与频道更新状态相互独立

- GitHub Release 负责人工下载、版本说明和两个 EXE 的摘要展示。
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
- **将更新文件改为 GitHub Actions Artifact：**Artifact 有保留期限，且客户端下载可能需要 GitHub 权限，不适合作为长期匿名更新源，因此不采用。
- **GitHub Pages + GitHub Releases：**可实现相同职责分离，但在当前已选用 Cloudflare DNS/域名管理的前提下，Cloudflare Pages 可直接使用同一账户的域名与部署控制面，因此优先采用 Cloudflare Pages。
- **Cloudflare R2：**适合托管大型静态对象，但当前不作为前置依赖；Feed 元数据体积小，Pages 已足够，避免 R2 开通可能涉及的支付验证障碍。
- **让 Installer 或 Bootstrapper 直接承担日常 ECL 更新下载：**会混淆首次安装介质、部署执行器与更新内容的职责；日常更新仍应由 Manifest 指向 ECL Package。

## 后果

- Release 页面从多个并列文件收敛为 Installer 与 Bootstrapper 两个 EXE，普通用户的下载决策更明确。
- 自动更新仍然使用签名 ECL Package，不会因为人工下载入口变化而改变部署协议。
- 发布系统需要同时维护人工 Release 与独立 Feed 两条发布结果，并为 Feed 托管增加持久性、带宽、缓存与可用性监控。
- 频道 Feed 成为自动更新的关键基础设施；Feed 故障不得降低签名验证要求，也不得阻止用户使用 Installer 完整离线恢复。
- Pages 的 Production Deployment 提供整站版本回滚；回滚必须恢复一套完整、此前验证过的频道元数据，而不是手工替换单个文件。
- Package 与 Feed 元数据由两个平台提供，发布流水线必须先确认 GitHub Release Package 可匿名访问，再发布 Pages 元数据，避免出现已发布 Manifest 指向尚不可下载的 Package。
- Manifest 当前的 Package 地址字段必须能够表达 GitHub Release 的绝对 HTTPS 下载地址；若现有客户端只支持相对 Feed 路径，必须先扩展并测试 URL 解析与主机/协议约束，再启用 Pages + Releases 分离，不能仅修改托管配置。
- 在 Feed 供应商与端点尚未完成验证前，旧 Release Feed 必须保持可用；因此本 ADR 的目标状态不能通过单纯删减 Release 附件提前宣称完成。

## 验收标准

1. 每个官方 Release 页面恰好包含两个可下载资产：对应架构的 Installer EXE 与 Bootstrapper EXE；不存在 ZIP、Manifest、Signature、trust store 或 checksum sidecar 附件。
2. Release Notes 明确说明 Installer 是普通用户入口、Bootstrapper 仅用于必要的独立修复，并分别列出与最终 EXE 字节一致的 SHA-256。
3. ECL 自动更新仍下载 Manifest 指定的完整 ECL Package，而不是 Installer 或 Bootstrapper。
4. `main`、`dev`、`nightly` 的 Feed 均可通过 `https://ender-craft-launcher-feed.pages.dev/<channel>/` 匿名 HTTPS 读取 Manifest 与 Signature；Manifest 指向 GitHub Releases 中不可变版本 Package。
5. Manifest 签名、包大小、SHA-256、逐文件哈希和频道/架构约束均通过客户端验证；篡改任一项必须 fail-closed。
6. Pages Production 部署不存在 Manifest 与 Signature 混配窗口；模拟中断发布后，客户端仍只能看到完整的旧部署或完整的新部署。
7. 新 Package 上传失败、签名失败或频道指针切换失败时，不得将频道推进到新版本；旧版本仍可下载和部署。
8. 回滚可恢复上一份已签名 Manifest/Signature，且不会删除仍被有效 Manifest 引用的 Package。
9. Actions Artifact 过期或不可访问，不影响客户端正常更新。
10. 从旧 Release Feed 迁移时，只有在新 Feed 的端到端验证通过且受支持客户端完成切换后，才移除旧 Release Feed 资产。
11. Feed 发布失败与 GitHub Release 发布成功必须被分别报告；不得把两者合并为单一成功状态。

## 修订记录

- 2026-11-15：首次决策。将人工 Release 与自动更新 Feed 分离；人工 Release 仅保留 Installer / Bootstrapper 两个 EXE，日常更新继续使用签名 ECL Package。
- 2026-11-15：明确采用 Cloudflare Pages Direct Upload 托管频道 Manifest/Signature，GitHub Releases 托管不可变 Package；由 Wrangler 在 Package 可匿名访问并验证后部署完整频道元数据目录。
