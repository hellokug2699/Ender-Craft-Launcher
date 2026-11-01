# ECL 安装程序

## 组件边界

- `ECL.Installer`：首次安装的 WPF 图形界面。
- `ECL.Bootstrapper`：独立部署进程，负责安全部署、启动和回滚。
- `ECL.Deployment`：Manifest、签名、包校验、安全解包、版本目录与 current 指针。
- 用户数据目录独立于程序安装目录。

## 当前 MVP 安装介质

将以下文件放在 `ECL.Installer.exe` 同一目录：

| 文件 | 用途 |
|---|---|
| `ECL.Bootstrapper.exe` | 独立部署入口 |
| `update-trust.json` | 发布者公钥信任库 |
| `update-manifest.json` | 版本、包和文件元数据 |
| `update-manifest.sig` | Manifest detached signature |
| `ECL-package.zip` | 被签名清单描述的完整程序包 |

安装器将 Bootstrapper 和公开信任库复制到所选安装根目录，然后通过 Bootstrapper CLI 部署。签名验证和解包不会在 GUI 进程内重复实现。

## 构建

```powershell
dotnet build src/ECL.Installer/ECL.Installer.csproj -c Release
dotnet publish src/ECL.Installer/ECL.Installer.csproj -c Release -r win-x64 --self-contained true
```

当前版本属于 MVP：安装介质由发布流水线或维护者准备；自动下载频道、安装包生成/签名流水线、静默参数、卸载器和 Authenticode 签名尚未实现。请勿把单独的 Installer EXE 当成完整可分发安装包。
