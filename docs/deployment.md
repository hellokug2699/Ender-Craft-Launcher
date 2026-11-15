# 部署、安装与更新

ECL 是多文件 WPF/.NET 应用，更新以**完整发布包**为单位，不替换单个 EXE，也不由运行中的主程序覆盖自身 DLL。
本页说明当前的 Deployment/Updater 实现、协议、发布流程和仍未完成的部分。

> 设计约束来自 private Core 仓库的 deployment hints，并由 ADR 0051 / 0071 固化公开架构边界。
> Installer 与 Uninstaller 已有独立 UI/编排实现；首次安装的单文件自包含分发及无运行时实机验收以本页“首次安装介质”与验证记录为准。

## 1. 目录模型

```text
<install>/
├─ ECL.Bootstrapper.exe
├─ update-trust.json          # 受信任的更新公钥（公开内容）
├─ current.json               # 当前版本指针
├─ versions/
│  ├─ 1.0.0/
│  └─ 1.0.1/
├─ staging/                   # 旁路准备；切换成功前可删除
└─ bootstrapper.log
```

- 程序安装目录与用户数据目录分离。更新、回滚和未来的卸载不得递归删除 Minecraft 实例、账户、配置或用户导入目录。
- `current.json` 只记录版本、相对可执行文件名、上一版本与安装时间；写入使用临时文件 + `File.Move(overwrite)` 原子替换。
- 每个版本位于独立目录；旧版本保留用于回滚。

## 2. Manifest 协议

Manifest schema v1，严格拒绝未知字段，避免旧客户端静默忽略新语义。

```json
{
  "schemaVersion": 1,
  "product": "ECL",
  "channel": "stable",
  "version": "1.0.1",
  "platform": "windows",
  "architecture": "x64",
  "minimumUpdaterVersion": "0.1.0",
  "package": {
    "file": "ECL-1.0.1-win-x64.zip",
    "size": 18308402,
    "sha256": "..."
  },
  "files": [
    { "path": "ECL.exe", "size": 123, "sha256": "..." }
  ],
  "removedFiles": [],
  "releaseNotes": "...",
  "publishedAt": "2026-11-01T00:00:00Z"
}
```

校验规则：

- `product` 必须为 `ECL`，`schemaVersion` 必须为当前支持值；
- `channel` 使用内部通道标识：`main`（正式版）、`dev`（测试版）、`nightly`（夜间版）；旧值 `stable` / `beta` 仅作为兼容输入。
- 版本号采用 semver 子集 `major.minor.patch[-prerelease]`，并用于版本目录名；
- 平台固定 `windows`，架构只接受 `x64` / `arm64`；
- 包大小与 SHA-256 必填；
- 文件路径只允许相对路径，拒绝盘符、绝对路径、`..`、重复路径和 Windows 非法字符；
- `files` 与 `removedFiles` 不能重叠；`removedFiles` 是显式白名单语义，当前安装器只解析/校验，尚未执行删除。

## 3. 签名与信任

签名是 detached signature：`update-manifest.sig` 对 `update-manifest.json` 的原始字节做 **RSA-PSS-SHA256**。

```json
{
  "schemaVersion": 1,
  "keyId": "ecl-release-2026-11",
  "algorithm": "RSA-PSS-SHA256",
  "signature": "base64..."
}
```

- 客户端内置/随安装分发的 `update-trust.json` 保存受信任公钥，按 `keyId` 查找。
- 未知 `keyId`、算法不符、签名错误、Manifest 格式错误全部 **fail-closed**。
- SHA-256 只用于完整性；发布者身份由 Manifest 签名提供。HTTPS 不能替代签名。
- 私钥位于 private Core 仓库 `signing/update-signing.key`；对应公钥在 public 仓库 `build/update-trust.json`。
- 密钥轮换时保留旧公钥一段时间，避免旧客户端无法验证中间版本；详见 Core 的 `signing/README.md`。

## 4. 更新流程

1. `UpdateClient` 只接受 HTTPS，读取通道的 `update-manifest.sig` 与 `update-manifest.json`；
2. 先验证签名，再解析 Manifest；
3. `UpdateCompatibility` 检查架构与最低 updater 版本；
4. `UpdatePackageVerifier` 下载后校验包大小与 SHA-256；
5. `SafeZipExtractor` 解包到 `staging/<version>-<guid>`，拒绝路径穿越、绝对路径、符号链接、重复条目与超限大小；
6. `UpdateInstaller.VerifyFilesAsync` 逐文件校验清单（大小 + SHA-256），默认拒绝清单外文件；
7. 完整后移动到 `versions/<version>`；
8. 原子写入 `current.json` 完成切换；失败时旧指针保持不变；
9. `RollbackAsync` 把指针切回 `previousVersion`，不删除当前版本目录。

运行中的 ECL 不会被覆盖：部署由独立的 `ECL.Bootstrapper.exe` 执行，主程序退出后再切换版本指针。

## 5. Bootstrapper

```powershell
# 正常启动 current.json 指向的版本
ECL.Bootstrapper.exe --install-root "C:\Program Files\ECL"

# 应用已经签名并下载到本地的更新包（建议在 ECL 退出后执行）
ECL.Bootstrapper.exe --install-root "C:\Program Files\ECL" --apply `
  --manifest "C:\updates\update-manifest.json" `
  --signature "C:\updates\update-manifest.sig" `
  --package "C:\updates\ECL-1.0.1-win-x64.zip" `
  --trust "C:\Program Files\ECL\update-trust.json" `
  --deploy-only

# 回滚到上一版本
ECL.Bootstrapper.exe --install-root "C:\Program Files\ECL" --rollback --deploy-only

# 清理异常中断留下的 staging（不触碰 current 指向的版本）
ECL.Bootstrapper.exe --install-root "C:\Program Files\ECL" --cleanup
```

退出码：`0` 成功；`2` 参数错误；`3` 信任/签名/公钥错误；`4` 部署失败；`5` 启动失败。该映射由 `BootstrapperExitCodes` 统一提供并被单元测试锁定，失败路径不得临时发明新的退出码。
引导程序使用 `.update.lock` 防止并发更新，日志写入 `bootstrapper.log`；`--apply`、`--rollback` 与 `--cleanup` 都在锁内执行。
部署前会自动清理未完成的 `staging/`，`versions/` 与 `current.json` 不受影响。
安装介质中的 Bootstrapper 以单文件自包含方式发布，首次安装不要求目标机器预装 .NET 运行时。

## 6. 设置与检查

现有配置是产品契约，更新模块沿用：

- `launcher.checkUpdates`：关闭时不得执行自动检查；
- `launcher.updateChannel`：`main` / `dev` / `nightly`（旧值 `stable` / `beta` 仍可读取并兼容映射）。\n\n- `main` 对应正式版；`dev` 对应测试版；`nightly` 对应夜间版。

当前 CLI 已接入设置：

```powershell
ecl update check --feed https://<update-feed-base>/ --trust .\update-trust.json
ecl update check --json
```

- `launcher.checkUpdates=false` 时直接跳过，不访问网络；
- Feed 地址优先级为 CLI `--feed` > 环境变量 `ECL_UPDATE_FEED` > 官方默认地址 `https://updates.endercraftlauncher.top/`；未显式配置时也会检查当前设置的频道；
- `--install-root` 可指定读取 `current.json` 的安装目录；否则使用 CLI 程序集版本。
- 主程序 UI 内的启动检查/下载入口尚未接入，属于下一阶段。

## 7. 发布

### 本地打包

```powershell
./build/pack-release.ps1 -Version 1.0.1 -Channel stable -SelfContained `
  -SigningKeyPath ".\signing\update-signing.key" `
  -KeyId "ecl-release-2026-11" `
  -TrustStorePath "build/update-trust.json"
```

输出：

```text
artifacts/release/1.0.1/
├─ ECL-1.0.1-win-x64.zip（签名 ECL Package；目标由独立 Feed 发布）
├─ bootstrapper/
│  └─ ECL.Bootstrapper.exe（目标人工 Release 的独立 EXE 来源）
├─ ECL.Installer-1.0.1-win-x64.exe（普通用户唯一推荐入口）
├─ ECL.Installer-1.0.1-win-x64.exe.sha256（构建中间摘要，不作为公开 Release 附件）
├─ installer-media/（完整部署介质，嵌入 Installer）
├─ installer-bundle.zip（构建中间产物）
├─ update-manifest.json（目标由独立 Feed 发布）
├─ update-manifest.sig（目标由独立 Feed 发布）
└─ update-trust.json（随客户端/Installer 分发的信任材料）
```

`-Unsigned` 只用于生成未签名的本地检查产物；由于完整安装介质必须包含签名清单，未签名模式不会生成可运行的 Installer。官方发布和安装介质验收必须使用有效签名。

### GitHub Actions：版本发布与滚动构建

[release.yml](../.github/workflows/release.yml) 已实现 ADR 0072 的主要发布链路；首次生产发布仍待 CI 与真实 Pages 部署验证。目标分工遵循 [ADR 0072](adr/0072-separation-of-human-facing-releases-and-channel-update-feeds.md)。

| 触发来源 | 构建追溯 | 人工 GitHub Release（目标） | 独立 Channel Feed（目标） | Package 存储（目标） |
|---|---|---|---|---|
| 推送 `nightly` | Actions Artifact，保留 14 天 | 更新唯一 Nightly 滚动 Release，仅两个 EXE | 更新 `nightly` Feed | 追加到 `packages/nightly/...` |
| 推送 `dev` | Actions Artifact，保留 14 天 | 更新唯一 Dev 滚动 Release，仅两个 EXE | 更新 `dev` Feed | 追加到 `packages/dev/...` |
| 推送 `vMAJOR.MINOR.PATCH` | Actions Artifact，保留 14 天 | 创建不可变版本 Release，并更新 `main` 滚动 Release；均仅两个 EXE | 更新 `main` Feed | 追加到 `packages/main/...` |
| 手动运行（仅 dev/nightly） | Actions Artifact，保留 14 天 | 更新对应滚动 Release | 更新对应频道 Feed | 追加到所选频道目录（须通过发布资格检查） |

发布实现已提交到当前 PR：客户端支持严格限定的 commit-pinned GitHub Raw Package URL、频道绑定和无自动重定向的更新 HTTP 客户端；发布脚本会迁移或验证其他频道的现有 Feed，将新 Package 追加到 `packages` 分支，签名并部署完整 Pages 站点。Feed 成功后再完成历史 Release 清理与频道滚动 Release 更新；正式版不可变 Release 会先以“Feed 待发布”状态创建，Feed 验证成功后才更新为“已发布”。Cloudflare Pages 项目与自定义域名已由 API 确认存在且域名状态为 active；目前尚无 Production Deployment。**尚未完成首次生产运行**：需要 CI 通过并在 nightly 上首次触发发布，验证匿名下载、签名、三频道 Feed 和 Release 资产后，才能认定迁移完成。

- Nightly 每次成功构建都会产生独立的 Actions Artifact，便于高级用户追溯特定构建；Artifact 保留 14 天，过期后不保证可取回。
- 普通用户下载 `ECL.Installer-<version>-win-x64.exe` 并直接运行；不需要下载 ZIP 或手工解压。
- `ECL.Bootstrapper-<version>-win-x64.exe` 是高级用户独立修复入口；它与 Installer 内嵌的 Bootstrapper 使用同一个自包含单文件构建。
- 两个 EXE 的 SHA-256 写入人工 Release Notes，不发布独立 checksum sidecar。
- `ECL-<version>-win-x64.zip` 是 Bootstrapper 日常更新/部署使用的完整程序包，不是首次安装入口；目标由独立 Channel Feed 提供。
- Installer 内嵌完整离线介质并在临时目录释放；实际签名验证、包解压和逐文件校验仍由 Bootstrapper 执行。
- Nightly 不为每次提交创建新的人工 Release；每个频道只维护一个滚动入口。
- Release tag 与 Feed 频道指针分别管理；只有 Feed 的 Package、Manifest、Signature 全部验证并原子切换后，才算自动更新发布成功。
- 同一分支的构建按队列执行；过期构建不得覆盖较新的 Release 或 Feed 版本。Cloudflare Pages Production 部署使用独立 `feed-lock` 分支租约串行化，避免跨频道完整站点互相覆盖。
- `packages` 分支仅允许 `packages/<channel>/...` 路径；单个 Package 必须小于 90 MiB，路径与文件名包含频道、架构、版本及 SHA-256，写入只追加且已发布包永久保留。
- 手动运行 workflow 时选择 `dev` 或 `nightly` 分支；正式版只能通过版本 Tag 发布。

构建流程执行私钥读取、公钥匹配、完整包构建、逐文件清单生成与 RSA-PSS 签名。私钥只存在于 runner 临时目录，并在 `always()` 清理步骤中删除。Actions Artifact 保留 14 天用于追溯。发布脚本先迁移/验证三个频道 Feed，再更新人工 Release；切换后的新建/更新 Release 只上传 Installer 与 Bootstrapper 两个 EXE，并在 Feed 成功后清理对应滚动 Release 的其他资产。已有可独立验证的 EXE 的历史 Release 可规范化；仅含旧 ZIP Installer 的历史 Release 保留原样，不能通过改名伪装成自包含 EXE。首次生产部署前，旧 Release Feed 资产仍须保留。

### Cloudflare Pages Feed 配置（目标部署方案）

Feed 项目采用 Direct Upload，不绑定 Git Integration：

- Cloudflare Pages 项目名：`ender-craft-launcher-feed`
- Production branch：`main`
- 正式公开地址：`https://updates.endercraftlauncher.top`
- Pages 默认地址 `https://ender-craft-launcher-feed.pages.dev` 保留用于部署验证与故障排查，不作为客户端正式 Feed 地址
- Feed 根目录：`/<channel>/`，频道为 `main`、`dev`、`nightly`
- 仅部署静态文件；不包含 `functions/`、`_worker.js` 或动态 API。
- Pages 仅承载 Manifest 与 detached Signature；完整 ECL Package 保留在同一公有仓库的 `packages` 分支。

首次控制台配置记录（当前项目与域名已由维护者确认创建并激活；重建项目时参考）：

1. Cloudflare Dashboard → Workers & Pages → Create application → Pages → Direct Upload。
2. 创建项目 `ender-craft-launcher-feed`，先部署临时静态目录完成初始化。
3. 在项目 Custom domains 中添加 `updates.endercraftlauncher.top`。
4. 确认 `endercraftlauncher.top` 已加入当前 Cloudflare 账户的有效 Zone，并按界面提示完成 DNS/域名激活。
5. 在 Account API Tokens 创建自定义 Token，只授予目标 Account 的 Cloudflare Pages: Edit 权限；不授予 Zone、DNS、Workers、R2 或账户管理权限。
6. 在 GitHub 仓库或 `release` Environment 的 Secrets 中配置签名密钥读取方式（二选一）：
   - `ECL_UPDATE_SIGNING_KEY`：完整 PEM 私钥内容；或
   - `ECL_CORE_READ_TOKEN`：仅授予 private Core 仓库 Contents: Read 的 fine-grained Token，供 workflow 临时读取 `signing/update-signing.key`。
7. 在 GitHub 仓库 Settings → Secrets and variables → Actions 添加：
   - `CLOUDFLARE_ACCOUNT_ID`：Cloudflare Account ID。
   - `CLOUDFLARE_API_TOKEN`：上述 Pages 专用 Token。
8. 所有 Token 与私钥都视为部署凭据，不得写入仓库、日志、Artifact 或客户端；workflow 仅在 runner 临时目录使用私钥，并在 `always()` 步骤清理。

CI 使用 Wrangler Direct Upload。静态目录必须在部署步骤前完整生成，并且只在 Package 已上传、可匿名下载且 SHA-256 校验成功后部署：

```bash
npx wrangler pages deploy artifacts/channel-feed \
  --project-name=ender-craft-launcher-feed \
  --branch=main
```

GitHub Actions 需要将 `CLOUDFLARE_ACCOUNT_ID` 与 `CLOUDFLARE_API_TOKEN` 作为环境变量注入部署步骤。建议固定 Wrangler 主版本/精确版本，并在 CI 中使用 lockfile，避免不受控的 CLI 版本漂移。

Direct Upload 项目的 Production branch 若需显式设置，通过 Pages Update Project API 将 `production_branch` 设为 `main`；不要假设 Dashboard 的 Git Integration 设置适用于 Direct Upload 项目。Preview 部署不得成为客户端 Feed 地址。

客户端 HTTP 兼容性注意：Cloudflare Pages 的 Browser Integrity Check（BIC）由 Pages 托管层执行，部分通用 HTTP 客户端默认 User-Agent 可能收到 HTTP 403 / Error 1010。ECL 的 Feed 请求必须发送稳定、可识别的产品 User-Agent（例如包含 ECL 产品名与版本），并在 Pages 自定义域名和 `pages.dev` 地址上分别进行真实客户端测试；不得通过关闭签名验证或放宽下载校验来规避访问问题。

Feed 发布检查：

- 发布脚本先获取独立 `feed-lock` 分支中的短期租约，串行化 Cloudflare Pages Production 部署；并发更新通过 Git compare-and-swap 重试，租约释放失败时会在到期后自动恢复。
- 发布脚本在静态目录写入 Pages `_headers` 文件，对频道元数据设置 `Cache-Control: no-store`；客户端跨部署版本读取 Manifest/Signature 时验签失败会重取整对文件一次。
- `packages` 分支只允许 `packages/<channel>/...` 路径；写入使用 Git tree/blob plumbing，不检出完整历史 Package。Package commit 只追加，不 force-push、不覆盖、不删除。
- 部署后分别请求 `https://updates.endercraftlauncher.top/main/update-manifest.json`、`https://updates.endercraftlauncher.top/dev/update-manifest.json`、`https://updates.endercraftlauncher.top/nightly/update-manifest.json` 及对应 `.sig`。
- 使用与客户端相同的信任根验证签名，并检查 Manifest 的 channel、platform、architecture、version 与 Package URL。
- 从匿名网络下载 Manifest 指向的 commit-pinned GitHub Raw Package，验证字节数、SHA-256 和逐文件清单。
- 任何一步失败都不得将该次发布报告为 Feed 成功；保留上一个已验证 Pages Production Deployment 以便回滚。

Package URL 兼容性已在当前分支实现：`package.file` 保留旧相对路径兼容，同时允许 `raw.githubusercontent.com/hellokug2699/Ender-Craft-Launcher/<commit-sha>/packages/<channel>/windows/<arch>/<version>/ECL-<version>-win-<arch>-<sha256>.zip`。解析器会校验 HTTPS、精确主机、仓库、40 位 commit SHA、频道、平台、架构、版本、文件名摘要与 Manifest SHA-256；更新专用 `SocketsHttpHandler` 禁用自动重定向，客户端拒绝 3xx 响应；Bootstrapper 远程修复也使用禁用重定向的 HTTP Handler。Pages Feed 使用 `_headers` 设置 `Cache-Control: no-store`，客户端请求要求缓存重新验证；若部署切换恰好发生在 Manifest 与 Signature 两次请求之间，客户端会重新读取整对文件一次，持续验签失败仍 fail-closed。CI 与首次生产下载仍需验证。

Pages Free 的单文件大小上限为 25 MiB、单站点最多 20,000 个文件；本方案只放小体积元数据，不把 ZIP Package 上传到 Pages。部署前应以 Cloudflare 当前官方限制再次核对。

### 当前实现与目标架构

**历史客户端协议：**迁移前的客户端使用 GitHub Release 下载路径：

```text
https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/<channel>/
```

其中 `<channel>` 为 `main`、`dev` 或 `nightly`。对应文件为 `update-manifest.json`、`update-manifest.sig` 与 Manifest 指定的完整 ECL Package。Feed 可通过 CLI 参数或 `ECL_UPDATE_FEED` 指定。

**当前 PR 的实现目标由 [ADR 0072](adr/0072-separation-of-human-facing-releases-and-channel-update-feeds.md) 定义：**GitHub Release 页面只提供 Installer 与 Bootstrapper 两个 EXE；Manifest/Signature 从 `https://updates.endercraftlauncher.top/<channel>/` 获取，完整 ECL Package 从公有仓库 `packages` 分支获取。客户端默认 Feed 已切换为官方 Pages 地址，同时保留 `ECL_UPDATE_FEED` 覆盖能力。首次生产部署完成后，旧版 GitHub Release Feed 资产将由发布流水线清理；在首次 Pages Production 部署成功前不得手工提前删除。

根据 [ADR 0051：统一安装、更新与恢复架构](adr/0051-unified-installation-update-and-recovery-architecture.md)，目标架构将由 Update Source Resolver 按有界策略尝试多个来源：

1. 独立官方 Channel Feed 直连；
2. 经允许列表配置的 Feed 镜像或代理传输来源；
3. 本地 Recovery Cache 与用户手动导入的完整离线介质。

上述顺序是目标策略，不代表这些来源当前均已实现或已配置。GitHub Actions Artifact 仍只用于构建追溯和人工下载，不作为客户端自动更新源：Artifact 有保留期限，下载也可能要求 GitHub 权限。

**信任边界不随来源变化：**所有来源均是不可信传输渠道。客户端必须使用预置/既有信任根验证 Manifest 签名，再验证 Manifest 语义、包大小、SHA-256 和逐文件清单。代理不得提供或替换信任公钥，不得接收 GitHub Token、访问令牌或签名私钥。代理及镜像的重定向目标必须逐跳检查；不得把任意 URL 当作可信代理。签名或哈希错误属于安全验证失败，不得通过降低校验级别继续部署。

本地 Recovery Cache 在执行修复时优先于联网下载。当前已实现按版本缓存已验证恢复材料及从显式 `ECL_UPDATE_FEED` 远程修复；Bootstrapper 自身损坏后的独立重建、完整离线恢复包手动导入和多来源 Resolver 仍是 ADR 0051 的后续实施范围。所有网络来源不可用且本地材料不足时，系统必须明确停止自动恢复，并指导用户通过另一台设备获取官方完整安装介质、传输到目标机器后进行离线验证与导入；不得伪称恢复成功。

本地构建指定提交：

```powershell
git checkout <commit-or-tag>
./build/pack-release.ps1 -Version 0.8.0-local.1 -Channel nightly `
  -SigningKeyPath ".\\signing\\update-signing.key" `
  -KeyId "ecl-release-2026-11" `
  -TrustStorePath "build/update-trust.json"
```

本地构建如不具备签名私钥，可使用 `-Unsigned` 仅验证打包过程；未签名产物不能作为正式更新源，也不能通过客户端签名校验。

## 8. 验证记录

2026-11-01，本地 Windows + 临时安装目录：

- `dotnet test --filter FullyQualifiedName~Ecl.Tests.Deployment`：37 项通过，覆盖版本解析、Manifest 校验、路径与重复条目、签名/未知公钥、安全解包、staging/切换/回滚、降级拒绝、HTTPS/下载校验。
- 端到端：用测试密钥签名 `0.8.0-e2e` → Bootstrapper `--apply --deploy-only` 写入 `current.json` → 启动到 `versions/0.8.0-e2e/ECL.exe`；再部署 `0.8.1-e2e` → `--rollback` 回到 `0.8.0-e2e`，两次退出码均为 0。
- 使用 Core 仓库的 `signing/update-signing.key` 与 public 仓库 `build/update-trust.json` 打包 `0.8.2-prodtest`，Bootstrapper 校验签名并完成部署，退出码 0；私钥与公钥指纹一致。
- GitHub Actions 实际 Release 测试（run `41220030330`）：通过 tag `v0.8.0-dev.1` 触发，成功生成 `v0.8.0-dev.1` 与 `beta` 两个 Release；`update-manifest.sig` 用 public 信任库验签通过，`beta` 资产 `ECL-0.8.0-dev.1-win-x64.zip` 的 GitHub digest 与 Manifest `package.sha256` 一致，job 日志未出现私钥标记。CI run `41220734282` 在 Linux 交叉路径修复后通过。

2026-11-02，安装介质端到端（本地 Windows、临时安装目录）：

- `pack-release.ps1` 用 Core 签名密钥打包两个版本（`0.8.1-local.1`、`0.8.2-local.1`），产出完整 `installer-media/` 并嵌入独立 Installer EXE；介质包含单文件自包含 Bootstrapper、Uninstaller、Manifest、签名、信任库与 `ECL-package.zip`。
- 完整程序包内包含 `ECL.LogHost.exe` 及其依赖，安装后由 Bootstrapper 启动 LogHost。
- 静默安装 `--silent --install-root <目录>`：退出码 0；生成 `current.json`、`versions/<版本>/ECL.exe`，未创建任何 Minecraft 工作区目录。
- 升级到第二个版本：退出码 0，`previousVersion` 正确指向旧版本，两个版本目录并存。
- `--rollback --deploy-only`：退出码 0，`current.json` 回到旧版本，版本目录内容未修改。
- 篡改 `update-manifest.json` 后重新安装：Bootstrapper 退出码 3、安装器退出码 4，未生成 `current.json` 或版本目录（fail-closed）。
- `--cleanup` 与部署前自动清理只删除 `staging/`；自动化测试确认 `versions/` 与 `current.json` 不受影响。
- 未完成：全新 Windows 无 .NET SDK、无 .NET 运行时的安装验收；Authenticode 签名；GUI 自动化（当前 GUI 仅人工冒烟）。
- 2026-11-15，ADR 0071 分发重构：官方构建切换为自包含单文件 Installer，完整部署介质嵌入 Installer；Bootstrapper 统一为单文件自包含构建，独立下载与 Installer 内嵌副本共用同一二进制。该项需等待 PR CI 与干净 Windows 实机验收后才能标记 Verified。
- 2026-11-15，ADR 0072 决定将人工 Release 与自动更新 Feed 分离：Cloudflare Pages Direct Upload 托管 Manifest/Signature，同一公有仓库的 `packages` 分支以 append-only 方式托管不可变 Package，Package URL 固定到 Git commit SHA。当前 PR 已实现发布脚本、三频道迁移、客户端绝对 Package URL 支持、无重定向请求及对已有独立 EXE 的历史 Release 资产规范化；仅含旧 ZIP Installer 的历史 Release 不会被伪装改名。首次生产部署、匿名下载和真实安装器验收仍待完成。旧 GitHub Release Feed 资产在新 Feed 端到端验证成功前不得清理。

## 9. 用户数据与卸载边界

更新与回滚只操作 `<install>` 下的 `versions/`、`staging/` 与 `current.json`，不触碰：

- `%LOCALAPPDATA%\EnderCraftLauncher` 或 `ECL_DATA_DIR` 下的账户、配置、日志、缓存；
- ECL 管理的实例/工作区；
- 用户导入的外部实例与共享 `.minecraft` 目录。

未来的卸载器默认保留这些数据；清理必须是独立、明确展示路径与数据类别的操作。

## 10. 后续

- 安装器（首次安装、快捷方式、卸载登记）与卸载清理选项；
- 主程序内启动检查、下载进度、更新提示与“退出后更新”；
- `removedFiles` 的安全执行与旧文件清理；
- Bootstrapper 自身更新、代码签名 Authenticode、SBOM/哈希发布清单；
- 差分更新、后台下载、镜像与静默更新。
