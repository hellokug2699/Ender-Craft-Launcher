# Ender Craft Launcher（ECL）

> **集成验证分支（dev）** · 面向 Windows 的 Minecraft 启动器，使用 C#、.NET 10 与 WPF 构建。

dev 是 ECL 的集成分支：用于汇总已完成的功能改动、验证跨模块协作，并作为进入 main 前的候选基线。它比 main 更新，但不保证与 nightly 完全同步，也不应被视为正式稳定发行版。使用前请备份实例目录、存档和重要配置。

## dev 当前功能基线

- **桌面界面**：Windows WPF，包含主页、下载、实例、内容、设置和关于页面，采用深色主题与持久侧栏。
- **主页与实例工作区**：从主页选择并启动实例；实例工作区集中管理实例信息、Java/JVM、账户、Mod、文件目录、启动记录和诊断。
- **下载中心**：搜索 Minecraft 版本、Mod、整合包、数据包、资源包和光影包；支持 CurseForge / Modrinth 来源筛选，以及游戏版本和加载器过滤。
- **游戏与加载器**：支持原版、Fabric、Forge、NeoForge 与 Cleanroom 安装及对应启动参数。
- **Java 管理**：探测本机 Java、按实例选择运行时、自动下载 Java，并提供版本兼容性检查和 JVM 参数预设。
- **账户**：支持离线账户和 Microsoft 正式账户。Microsoft 登录采用 OAuth Authorization Code + PKCE，经 Xbox Live、XSTS 和 Minecraft Services 验证 Java Edition 权益与档案；敏感凭据使用 Windows DPAPI 保存，并支持静默刷新。
- **内容管理**：管理 Mod、资源包、光影包和数据包；检查常见 Mod 元数据问题。
- **整合包**：导入 Modrinth .mrpack 和 CurseForge .zip，支持 Modrinth 格式导出，并对导入文件进行校验与安全解压。
- **内容平台**：统一接入 CurseForge 与 Modrinth 搜索和安装；Modrinth 依赖可递归解析，CurseForge 请求经项目代理服务处理。
- **启动与诊断**：启动前检查、版本解析、Java 选择、文件校验、natives 解压和进程启动；支持崩溃日志分析与脱敏诊断包。
- **CLI 与自动化测试**：提供无图形命令行入口；Core / CLI / 测试项目可在支持的平台上构建和测试。

## 当前已知缺口

以下项目尚未在 dev 基线中交付，不应把 nightly 的实现状态误认为 dev 已具备：

- Authlib-Injector / Yggdrasil 第三方认证。
- 扫描并导入外部 Minecraft 安装，以及共享游戏目录的实例路径适配。
- Mod 更新检查、版本范围语义和 CurseForge 可选依赖选择。
- 实例原地切换 Minecraft 版本 / 加载器、实例迁移和备份恢复。
- 正式安装包、发布签名验证和启动器自更新。
- Quilt、Legacy Fabric、首次启动向导、托盘和完整语言热切换。

CurseForge 作者限制第三方分发的文件仍须遵循来源平台的授权和下载流程。

## 测试与反馈

dev 的目的就是验证集成质量。提交问题时请尽量附上：

1. ECL 版本和构建时间。
2. Windows 版本、Java 发行版与版本、Minecraft 版本和加载器版本。
3. 可复现步骤，以及预期结果和实际结果。
4. 脱敏后的启动日志或诊断包。分享前请检查本机用户名、文件路径和其他个人信息。

不要在 Issue、日志或截图中公开访问令牌、密码、API key 或其他凭据。Mock 测试不能替代真实账户或真实服务器的实机验证。

## 获取代码与构建

```powershell
git clone https://github.com/hellokug2699/Ender-Craft-Launcher.git
cd Ender-Craft-Launcher
git switch dev
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
dotnet run --project src/ECL.Cli -- instance create "Survival" --version 1.21.1
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
dotnet run --project src/ECL.Cli -- mod list <实例ID>
dotnet run --project src/ECL.Cli -- diagnostics export <实例ID>
```

Windows WPF 程序仅面向 Windows 运行；Linux / macOS 可用于构建和测试 Core、CLI 与相关项目。

## 分支工作流

```text
nightly  ──持续开发──>  dev  ──集成验证与验收──>  main
```

- main：正式主线，优先稳定性。
- dev：集成验证，确认功能协作、回归和 CI。
- nightly：快速迭代，允许暂未完成的功能与界面变化。

合并前必须重新检查目标分支最新提交，不得覆盖维护者的新推送。只有相关 CI 全部 completed 且 success、必要实机验收完成后，才能将变更视为通过。

## 文档与仓库

- [架构总览](docs/architecture.md)
- [认证说明](docs/authentication.md)
- [启动流水线](docs/launch-pipeline.md)
- [诊断说明](docs/diagnostics.md)
- [开发路线图](docs/roadmap.md)
- [仓库与保密策略](docs/repository-policy.md)

公开仓库 Ender-Craft-Launcher 存放源码与文档。私有仓库 Ender-Craft-Launcher-Core 仅用于签名材料和其他敏感内容。公开仓库常规构建不得依赖私有仓库；严禁提交私钥、令牌、密码或 API key。

## 许可证

GPL-3.0-or-later，详见 [LICENSE](LICENSE)。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang Studios 或 Microsoft 无关联。
