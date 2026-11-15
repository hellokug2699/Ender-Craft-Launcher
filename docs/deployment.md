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
- 没有 `--feed` 且未设置 `ECL_UPDATE_FEED` 时只提示未配置，不发起请求；
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

[release.yml](../.github/workflows/release.yml) 当前仍处于迁移前状态；目标分工遵循 [ADR 0072](adr/0072-separation-of-human-facing-releases-and-channel-update-feeds.md)。

| 触发来源 | 构建追溯 | 人工 GitHub Release（目标） | 独立 Channel Feed（目标） |
|---|---|---|---|
| 推送 `nightly` | Actions Artifact，保留 14 天 | 更新唯一 Nightly 滚动 Release，仅两个 EXE | 更新 `nightly` Feed |
| 推送 `dev` | Actions Artifact，保留 14 天 | 更新唯一 Dev 滚动 Release，仅两个 EXE | 更新 `dev` Feed |
| 推送 `vMAJOR.MINOR.PATCH` | Actions Artifact，保留 14 天 | 创建不可变版本 Release，并更新 `main` 滚动 Release；均仅两个 EXE | 更新 `main` Feed |
| 手动运行 | Actions Artifact，保留 14 天 | 根据所选分支更新对应人工 Release | 更新对应频道 Feed |

当前 workflow 仍把更新资产放在 GitHub Release；独立 Feed 尚未选定供应商与端点。表格描述的是迁移目标，不代表现有流水线已经完成切换。

- Nightly 每次成功构建都会产生独立的 Actions Artifact，便于高级用户追溯特定构建；Artifact 保留 14 天，过期后不保证可取回。
- 普通用户下载 `ECL.Installer-<version>-win-x64.exe` 并直接运行；不需要下载 ZIP 或手工解压。
- `ECL.Bootstrapper-<version>-win-x64.exe` 是高级用户独立修复入口；它与 Installer 内嵌的 Bootstrapper 使用同一个自包含单文件构建。
- 两个 EXE 的 SHA-256 写入人工 Release Notes，不发布独立 checksum sidecar。
- `ECL-<version>-win-x64.zip` 是 Bootstrapper 日常更新/部署使用的完整程序包，不是首次安装入口；目标由独立 Channel Feed 提供。
- Installer 内嵌完整离线介质并在临时目录释放；实际签名验证、包解压和逐文件校验仍由 Bootstrapper 执行。
- Nightly 不为每次提交创建新的人工 Release；每个频道只维护一个滚动入口。
- Release tag 与 Feed 频道指针分别管理；只有 Feed 的 Package、Manifest、Signature 全部验证并原子切换后，才算自动更新发布成功。
- 同一分支的构建按队列执行；过期构建不得覆盖较新的 Release 或 Feed 版本。
- 手动运行 workflow 时选择 `dev` 或 `nightly` 分支；正式版只能通过版本 Tag 发布。

构建流程仍然执行私钥读取、公钥匹配、完整包构建、逐文件清单生成与 RSA-PSS 签名。私钥只存在于 runner 临时目录，并在 `always()` 清理步骤中删除。Actions Artifact 包含完整构建中间产物。迁移完成后，人工 Release 只包含 Installer 与 Bootstrapper 两个 EXE；独立 Feed 提供完整 ECL Package、Manifest 与 Signature。当前发布流水线尚未完成该迁移，旧 Feed 资产必须保留。

### 当前实现与目标架构

**当前实现仍处于迁移前状态：**自动更新/远程修复默认使用 GitHub Release 下载路径：

```text
https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/<channel>/
```

其中 `<channel>` 为 `main`、`dev` 或 `nightly`。对应文件为 `update-manifest.json`、`update-manifest.sig` 与 Manifest 指定的完整 ECL Package。Feed 可通过 CLI 参数或 `ECL_UPDATE_FEED` 指定。

**目标状态由 [ADR 0072](adr/0072-separation-of-human-facing-releases-and-channel-update-feeds.md) 定义：**GitHub Release 页面只提供 Installer 与 Bootstrapper 两个 EXE；Manifest、Signature 与完整 ECL Package 从独立的匿名 HTTPS Channel Feed 获取。独立 Feed 的供应商与正式端点尚未选定，因此当前不得提前从 GitHub Release 删除这些旧 Feed 资产，否则现有客户端自动更新会中断。切换必须先完成独立 Feed 的匿名下载、签名验证、包哈希、频道发布原子性与回滚验收，再更新客户端默认 Feed 并清理旧资产。

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
- 2026-11-15，ADR 0072 决定将人工 Release 与自动更新 Feed 分离。当前 release workflow 尚未完成迁移，旧 GitHub Release Feed 资产必须保留，直到独立 Feed 通过端到端验证。

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
