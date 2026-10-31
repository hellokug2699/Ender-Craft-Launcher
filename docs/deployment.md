# 部署、安装与更新

ECL 是多文件 WPF/.NET 应用，更新以**完整发布包**为单位，不替换单个 EXE，也不由运行中的主程序覆盖自身 DLL。
本页说明当前的 Deployment/Updater 实现、协议、发布流程和仍未完成的部分。

> 设计约束来自 private Core 仓库的 deployment hints。当前实现已经覆盖“协议 + 侧旁 staging + 指针切换 + 回滚 + 发布产物”，
> 但安装器 UI、卸载、主程序内自动检查/下载以及差分更新仍属于后续阶段。

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
- `channel` 只接受 `stable` / `beta` / `dev`；
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
```

退出码：`0` 成功；`2` 参数错误；`3` 信任/签名/公钥错误；`4` 部署失败；`5` 启动失败。
引导程序使用 `.update.lock` 防止并发更新，日志写入 `bootstrapper.log`。

## 6. 设置与检查

现有配置是产品契约，更新模块沿用：

- `launcher.checkUpdates`：关闭时不得执行自动检查；
- `launcher.updateChannel`：`stable` / `beta` / `dev`。

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
./build/pack-release.ps1 -Version 1.0.1 -Channel stable `
  -SigningKeyPath "D:\Core\signing\update-signing.key" `
  -KeyId "ecl-release-2026-11" `
  -TrustStorePath "build/update-trust.json"
```

输出：

```text
artifacts/release/1.0.1/
├─ ECL-1.0.1-win-x64.zip
├─ ECL.Bootstrapper-1.0.1-win-x64.zip（由 Release workflow 收集）
├─ bootstrapper/
├─ update-manifest.json
├─ update-manifest.sig
└─ update-trust.json
```

`-Unsigned` 只用于本地检查；未签名清单不会通过客户端验证。

### GitHub Actions / Release

[release.yml](../.github/workflows/release.yml) 在 tag `v*` 或手动触发时：

1. 校验版本与通道；
2. 从 Actions Secret `ECL_UPDATE_SIGNING_KEY`（推荐）或私有 Core 的 `ECL_CORE_READ_TOKEN` 读取私钥；
3. 校验私钥导出的公钥与 `build/update-trust.json` 完全一致；
4. 构建完整包、生成逐文件清单并用 RSA-PSS 签名；
5. 创建 `v<version>` 与通道 tag（`stable` / `beta` / `dev`）的 GitHub Release，上传 zip、Manifest、签名、信任库与 Bootstrapper 包；
6. 在 `always()` 步骤删除 runner 上的临时私钥。

客户端更新源基址形如：

```text
https://github.com/hellokug2699/Ender-Craft-Launcher/releases/download/<channel>/
```

对应文件：`<base>/update-manifest.json`、`<base>/update-manifest.sig`、`<base>/<package.file>`。

> `release` Environment 建议配置 Required reviewers；workflow 只授予 `contents: write`，checkout 不保留凭据。
> 私钥不得写入日志、Release 附件、artifact 或 public 仓库。CI 的 PR 流程永远不会签名。

## 8. 验证记录

2026-11-01，本地 Windows + 临时安装目录：

- `dotnet test --filter FullyQualifiedName~Ecl.Tests.Deployment`：37 项通过，覆盖版本解析、Manifest 校验、路径与重复条目、签名/未知公钥、安全解包、staging/切换/回滚、降级拒绝、HTTPS/下载校验。
- 端到端：用测试密钥签名 `0.8.0-e2e` → Bootstrapper `--apply --deploy-only` 写入 `current.json` → 启动到 `versions/0.8.0-e2e/ECL.exe`；再部署 `0.8.1-e2e` → `--rollback` 回到 `0.8.0-e2e`，两次退出码均为 0。
- 未完成：全新 Windows 无 .NET SDK 的安装验收、安装器 UI、卸载、真实签名 Release、125%/150% 缩放下的更新 UI。

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
