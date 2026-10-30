# Ender Craft Launcher（ECL）

> **nightly 开发分支说明** · 面向 Windows 的 Minecraft 启动器，使用 C#、.NET 10 与 WPF 构建。

ECL 致力于提供完整、可靠且易于维护的 Minecraft 实例管理与启动体验。项目以成熟启动器为参考，同时建立自己的配置、安装、下载和诊断体系。

本分支是持续开发中的 nightly 版本，功能、界面和内部结构都可能调整。请勿将 nightly 视为稳定发行版；使用前建议备份实例目录和重要游戏数据。

## 项目现状

- **桌面界面**：Windows WPF，采用深色主题与持久侧栏，包含主页、下载、实例、内容、设置和关于页面。
- **主页工作台**：选择并启动实例、预览启动命令、查看 Java 与账户状态、快速切换实例，并提供下载中心、实例管理和已安装内容入口。主页不展示 Minecraft 正式版或快照版更新资讯。
- **下载中心**：统一搜索游戏版本、Mod、整合包、数据包、资源包和光影包；支持 CurseForge、Modrinth 来源筛选、游戏版本与加载器过滤、查看项目详情、下载或安装。
- **实例独立管理**：每个实例拥有独立设置文件，可分别指定 Java 可执行文件、JVM 参数和内存；支持打开实例目录与 Mods 目录、直接进入该实例的模组管理，以及导出最近一次运行的 `latest.log`。留空的实例覆盖项继承全局设置。
- **游戏安装**：支持原版、Fabric、Forge、NeoForge 与 Cleanroom 的安装流程，并处理对应的版本元数据、依赖库和启动参数。
- **Java 管理**：探测本机 Java，按实例选择运行时；支持自动下载运行时、版本兼容性检查和 JVM 参数预设。
- **账户**：支持离线账户与默认账户设置。
- **内容管理**：解析 Mod 元数据，检查重复项、依赖缺失、冲突和加载器不匹配；支持导入、启用、禁用与删除。
- **整合包**：支持导入 Modrinth .mrpack 与 CurseForge .zip，并提供 Modrinth 格式导出；导入过程包含文件校验与安全解压保护。
- **资源与光影**：管理资源包、光影包和数据包；相关启用状态与 Minecraft 配置保持同步。
- **CurseForge / Modrinth**：统一搜索与安装入口；Modrinth 依赖可递归解析，CurseForge 请求通过项目代理服务处理。
- **启动与诊断**：执行启动前检查、版本解析、Java 选择、文件校验、原生库解压和进程启动；支持崩溃日志分析及脱敏诊断包导出。
- **下载控制**：下载任务显示进度与速度，并支持暂停和继续。
- **命令行**：提供无图形界面的安装、实例、Java、Mod、整合包、配置与启动命令。
- **自动化测试**：核心逻辑由单元测试覆盖；每次推送以 GitHub Actions 中的构建和测试结果为准。

## 尚未实现或仍有限制

- Microsoft 正式账户登录及第三方认证尚未接入。
- Quilt、Legacy Fabric 等加载器尚未支持。
- Mod 更新检查与可选依赖交互选择尚未完成。
- CurseForge 作者限制第三方分发的文件无法由启动器代为分发。
- 启动器自动更新、正式安装包和代码签名发布尚未完成。
- 首次启动向导、托盘和完整的语言热切换等体验功能仍在规划中。

具体开发优先级以 [开发路线图](docs/roadmap.md) 为准。文档描述可能落后于 nightly 实际代码；若有冲突，以代码和自动化测试为准。

## 环境要求

- Windows 10 1809 或更高版本 / Windows 11（运行 WPF 桌面程序）
- .NET 10 SDK（仓库使用 global.json 锁定 SDK）
- Java 运行时：根据 Minecraft 版本选择 Java 8、17、21、25 等兼容版本；也可由 ECL 协助下载

Linux 和 macOS 可用于构建 Core、CLI 与测试项目；WPF 桌面程序仅面向 Windows。

## 获取代码

~~~powershell
git clone https://github.com/hellokug2699/Ender-Craft-Launcher.git
cd Ender-Craft-Launcher
git switch nightly
~~~

## 构建、测试与运行

在仓库根目录执行：

~~~powershell
dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet format ECL.sln --verify-no-changes
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
dotnet run --project src/ECL.App
~~~

运行命令行工具示例：

~~~powershell
dotnet run --project src/ECL.Cli -- version
dotnet run --project src/ECL.Cli -- account add-offline Steve --default
dotnet run --project src/ECL.Cli -- instance create "Survival" --version 1.21.1
dotnet run --project src/ECL.Cli -- instance create "Fabric" --version 1.21.1 --loader fabric
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- java install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
dotnet run --project src/ECL.Cli -- mod list <实例ID>
dotnet run --project src/ECL.Cli -- mod check <实例ID>
dotnet run --project src/ECL.Cli -- modpack import <实例ID> "C:\packs\example.mrpack"
dotnet run --project src/ECL.Cli -- diagnostics export <实例ID>
~~~

可使用 --data-dir <目录> 指定独立数据目录，使用 --lang zh-CN|en-US 指定界面语言。

## 仓库结构

~~~text
src/
  ECL.Core/            通用基础设施、本地化、路径与结果类型
  ECL.Configuration/   元数据驱动配置系统
  ECL.Download/        HTTP 下载、镜像策略、校验与续传
  ECL.Auth/            账户模型与离线账户
  ECL.Java/            Java 探测、兼容性与运行时安装
  ECL.Minecraft/       实例、版本、加载器、内容、诊断与启动流程
  ECL.App/             WPF 桌面界面（MVVM）
  ECL.Cli/             命令行入口
tests/
  ECL.Tests/           自动化单元测试
docs/                  架构文档、使用说明、路线图与 ADR
build/                 构建与发布脚本
~~~

## 数据位置

默认数据目录位于：

~~~text
%LOCALAPPDATA%\EnderCraftLauncher
~~~

实例和游戏文件默认保存在该目录下，也可以在设置中指定其他 .minecraft 目录。环境变量 ECL_GAME_DIR 可覆盖游戏目录。详细布局见 [架构说明](docs/architecture.md)。

## 仓库与敏感信息

- [Ender-Craft-Launcher](https://github.com/hellokug2699/Ender-Craft-Launcher)：公开仓库，存放源代码、文档、构建脚本和公开配置。
- [Ender-Craft-Launcher-Core](https://github.com/hellokug2699/Ender-Craft-Launcher-Core)：私有仓库，仅用于存放不应公开的签名材料及其他敏感内容。

公开仓库的常规构建不依赖私有 Core 仓库。不得将证书私钥、OAuth 凭据、API 密钥或其他秘密提交到公开仓库。具体规则见 [仓库与保密策略](docs/repository-policy.md)。

## 文档索引

- [架构总览](docs/architecture.md)
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

本项目采用 GPL-3.0-or-later，详见 [LICENSE](LICENSE)。

Minecraft 是 Mojang Studios 的商标。本项目与 Mojang Studios 或 Microsoft 无关联。

界面字体 HarmonyOS Sans SC 的版权归 Huawei Device Co., Ltd. 所有，并依据其字体许可协议随程序分发；许可全文见 [字体许可文件](src/ECL.App/Fonts/HarmonyOS_Sans_LICENSE.txt)。
