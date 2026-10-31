# Ender Craft Launcher（ECL）

> **持续开发分支（nightly）** · 面向 Windows 的 Minecraft 启动器，使用 C#、.NET 10 与 WPF 构建。

nightly 是 ECL 的主要活动开发分支，也是新功能、架构调整和快速修复首先集成的地方。这里的功能可能尚未经过完整实机验证，界面、配置和内部接口也可能继续变化。**nightly 不等于稳定发行版**；使用前请备份实例目录、存档和重要配置。

## 当前开发基线

- **桌面工作台**：主页、下载、实例、内容、设置和关于页面；主页用于启动与状态入口，下载集中在下载中心。
- **实例工作区**：实例库负责展示和入口；每个实例拥有独立工作区，管理基本信息、账户、Java/JVM、Mod、文件、启动记录和崩溃诊断。
- **外部实例导入**：扫描 Minecraft versions 目录，识别共享游戏目录和版本隔离目录；导入引用既有文件，不复制原安装。启动、内容管理和诊断使用实例实际游戏目录。
- **游戏与加载器**：支持原版、Fabric、Forge、NeoForge 与 Cleanroom 安装和启动。
- **Java 管理**：扫描 Java 安装，包括多 JDK 父目录；按实例选择运行时，支持自动下载、兼容性检查和 JVM 预设。
- **账户**：支持离线、Microsoft 和 Authlib-Injector / Yggdrasil 第三方账户。Microsoft 使用 OAuth Authorization Code + PKCE，并经 Xbox Live、XSTS、Minecraft Services 验证；敏感凭据使用 Windows DPAPI 保护。第三方账户使用独立认证服务器配置，密码仅用于登录过程，不长期保存。
- **第三方认证安全**：认证服务器要求 HTTPS（仅 localhost / loopback 允许 HTTP），拒绝不安全重定向；修改认证服务器地址前需重新认证；Injector 下载产物校验 SHA-256，并验证缓存路径与版本元数据。
- **内容管理**：管理 Mod、资源包、光影包和数据包；检查常见 Mod 元数据问题。
- **内容平台与整合包**：支持 Modrinth / CurseForge 搜索与安装、Modrinth 依赖解析、Modrinth .mrpack 与 CurseForge .zip 导入，以及 Modrinth 格式导出。
- **启动与诊断**：执行启动前检查、版本解析、Java 选择、文件校验、natives 解压和进程启动；提供崩溃分析与脱敏诊断包。
- **CLI 与自动化测试**：提供无图形命令行入口；Windows CI 验证 WPF 构建，Linux CI 验证 Core / CLI 与测试项目。

## 当前路线图

开发顺序以 [docs/roadmap.md](docs/roadmap.md) 为准：

1. **实例管理可靠性**：导入实例回归、目录与文件所有权、实例迁移、原地变更版本和加载器。
2. **内容生命周期**：Mod 更新检查、更新预览与批量更新、整合包更新、下载中心分页和筛选。
3. **日常使用体验**：账户中心、实例工作区收尾、可访问性与诊断、备份和恢复。
4. **可分发版本**：安装包、签名与更新协议、干净环境安装升级测试、v1.0 发布。

路线图中的每项工作都有交付物和验收条件。未通过测试或必要实机验收的任务不得仅凭代码已提交标记完成。

## 已知未完成 / 暂不纳入近期主线

- Mod 更新检查、严格版本范围语义、可选依赖选择和整合包更新。
- 实例迁移、原地切换 Minecraft 版本 / 加载器、备份恢复。
- 完整账户中心、注销和 refresh token 失效后的重新授权体验。
- 正式安装包、签名验证和启动器自更新。
- Quilt / Legacy Fabric、托盘、首次启动向导和完整语言热切换暂不排入近期主线。
- CurseForge 作者限制第三方分发的文件无法由启动器绕过来源限制。

## 获取代码与构建

```powershell
git clone https://github.com/hellokug2699/Ender-Craft-Launcher.git
cd Ender-Craft-Launcher
git switch nightly
dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet format ECL.sln --verify-no-changes
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
dotnet run --project src/ECL.App
```

CLI 示例：

```powershell
dotnet run --project src/ECL.Cli -- version
dotnet run --project src/ECL.Cli -- account add-offline Steve --default
dotnet run --project src/ECL.Cli -- instance scan
dotnet run --project src/ECL.Cli -- instance import <版本目录>
dotnet run --project src/ECL.Cli -- instance create "Survival" --version 1.21.1
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
dotnet run --project src/ECL.Cli -- mod list <实例ID>
dotnet run --project src/ECL.Cli -- mod check <实例ID>
dotnet run --project src/ECL.Cli -- modpack import <实例ID> "C:\packs\example.mrpack"
dotnet run --project src/ECL.Cli -- diagnostics export <实例ID>
```

环境要求：

- Windows 10 1809 或更高版本 / Windows 11：运行 WPF 桌面程序。
- .NET 10 SDK：具体版本以仓库 global.json 为准。
- Java：按 Minecraft 版本选择兼容运行时，也可由 ECL 下载。
- Linux / macOS 可用于构建和测试 Core、CLI；WPF 仅支持 Windows 运行。

## 分支工作流

```text
nightly  ──持续开发──>  dev  ──集成验证与验收──>  main
   │                         │                         │
   └─快速迭代 / 实验           └─候选集成基线             └─正式稳定基线
```

- 不要把 nightly 的功能状态写成 dev 或 main 已支持。
- 不要未经维护者确认重置、删除或强推覆盖分支。
- 合并前重新读取源分支与目标分支 SHA，防止覆盖新提交。
- CI 必须显示 completed 且 success；必要的真实账户、真实游戏和安装包场景还要完成实机验收。

## 仓库、安全与反馈

- [公开仓库：Ender-Craft-Launcher](https://github.com/hellokug2699/Ender-Craft-Launcher)
- [私有仓库：Ender-Craft-Launcher-Core](https://github.com/hellokug2699/Ender-Craft-Launcher-Core)
- [提交问题与反馈](https://github.com/hellokug2699/Ender-Craft-Launcher/issues)

公开仓库存放源码、文档、构建脚本和公开配置；私有 Core 仓库只用于签名材料及其他敏感内容。公开仓库普通构建不得依赖私有仓库。禁止提交证书私钥、密码、访问令牌或 API key。OAuth Client ID 是公开标识符，不是 Client Secret。

提交问题时请提供构建版本、操作系统、Minecraft / 加载器 / Java 版本、复现步骤和脱敏日志。分享诊断包前检查本机用户名、文件路径和其他隐私信息。

## 文档

- [架构总览](docs/architecture.md)
- [认证说明](docs/authentication.md)
- [配置系统](docs/configuration.md)
- [Java 管理](docs/java.md)
- [加载器与组件](docs/loaders.md)
- [Mod、整合包与资源包](docs/mods.md)
- [CurseForge 集成](docs/curseforge.md)
- [Modrinth 集成](docs/modrinth.md)
- [崩溃分析与诊断包](docs/diagnostics.md)
- [启动流水线](docs/launch-pipeline.md)
- [仓库与保密策略](docs/repository-policy.md)
- [开发路线图](docs/roadmap.md)
- [架构决策记录（ADR）](docs/adr)

## 许可证

GPL-3.0-or-later，详见 [LICENSE](LICENSE)。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang Studios 或 Microsoft 无关联。
