# ECL 安装程序

## 组件边界

- `ECL.Installer`：首次安装/升级的 WPF 图形界面与静默模式入口。
- `ECL.Bootstrapper`：独立部署进程，负责安全部署、启动和回滚。
- `ECL.Deployment`：Manifest、签名、包校验、安全解包、版本目录、current 指针与 `InstallerService`。
- 用户数据目录与 Minecraft 工作区独立于程序安装目录。

安装器不实现第二套解包、Manifest 解析或签名逻辑：它校验介质完整性、复制 Bootstrapper 与公开信任库，然后调用稳定的 Bootstrapper CLI。

## 安装介质协议

安装器同目录必须存在：

| 文件 | 用途 |
|---|---|
| `ECL.Bootstrapper.exe` | 独立部署入口 |
| `update-trust.json` | 发布者公钥信任库 |
| `update-manifest.json` | 版本、包和文件元数据 |
| `update-manifest.sig` | Manifest detached signature |
| `ECL-package.zip` | 被签名清单描述的完整程序包 |

`InstallMedia.Inspect` 在启动部署前检查全部文件；签名、包哈希与逐文件校验由 Bootstrapper 执行。默认安装位置为当前用户 `%LOCALAPPDATA%\Programs\Ender Craft Launcher`，不要求管理员权限。

## 图形界面

- 选择安装目录；检测目标目录是否已有部署，已有部署按升级处理，不静默覆盖或破坏 `current.json`。
- 显示介质状态、阶段进度、可取消的部署过程与实时 `bootstrapper.log`。
- 失败时展示 Bootstrapper 退出码含义与日志入口；不会破坏原有 `current` 指针。
- 成功后提供“启动 ECL”，经 `ECL.Bootstrapper.exe` 启动当前版本，而不是直接运行版本目录内的 EXE。
- 安装器不创建、迁移或清理 Minecraft 工作区。

## 静默安装

```powershell
ECL.Installer.exe --silent --install-root "<安装目录>" [--media "<介质目录>"] [--package "<包路径>"]
```

- `--install-root` 必须是绝对路径，且不能是磁盘根目录。
- 退出码：`0` 成功，`2` 参数或介质错误，`4` 部署失败。
- 结果写入 `<安装目录>\installer-silent.log`；不弹出 UI。
- 与 GUI 共用 `InstallerService`，不存在第二套部署实现。

## 构建与发布

```powershell
dotnet build src/ECL.Installer/ECL.Installer.csproj -c Release
pwsh build/pack-release.ps1 -Version 1.0.1 -SigningKeyPath <私钥> -TrustStorePath build/update-trust.json
```

`pack-release.ps1` 产出：

```text
artifacts/release/<version>/
├─ ECL-<version>-win-x64.zip          # 完整程序包（含 ECL.LogHost）
├─ update-manifest.json
├─ update-manifest.sig
├─ update-trust.json
├─ bootstrapper/
│  └─ ECL.Bootstrapper.exe
└─ installer/                          # 可直接分发的安装介质
   ├─ ECL.Installer.exe
   ├─ ECL.Bootstrapper.exe
   ├─ update-manifest.json
   ├─ update-manifest.sig
   ├─ update-trust.json
   └─ ECL-package.zip
```

Release workflow 将 `installer/` 打包为 `ECL.Installer-<version>-win-x64.zip` 并随版本资产发布。未签名构建不能作为正式安装介质。

## 尚未完成

- 安装包 Authenticode 签名、卸载器与自动下载安装介质。
- 干净 Windows 环境下的安装/升级/回滚/卸载自动化验收。
