# Ender Craft Launcher

> 一个面向 Windows 的 Minecraft 启动器，使用 C# / .NET 10 / WPF 构建。

ECL 的目标是达到 HMCL、PCL2 级别的完整度，同时在配置系统、启动流程和交互上做出自己的设计。当前仓库处于 **v0.7 体验与诊断** 阶段（v0.3 账户体系因 Microsoft OAuth 审核限制暂时跳过），并已补全 CurseForge API 集成：能够安装原版、Fabric（含 Fabric API）、Forge、NeoForge 与 Cleanroom，管理 Java 运行时、Mod、整合包、资源包与光影包，完成真实启动，并提供主题切换、崩溃分析与脱敏诊断包。首次启动向导本阶段暂缓。

## 现在能做什么

- WPF 桌面壳：实例库、Mod 管理、设置、关于四个页面，支持深色/浅色主题即时切换。
- 多实例管理：创建/列出/删除实例，每个实例有独立目录与元数据。
- 离线账户：按 Java `UUID.nameUUIDFromBytes` 规则生成 UUID，可设默认账户。
- 元数据驱动配置：全局设置与实例覆盖，自动生成设置页，支持校验、可见性依赖、来源标记。
- **原版安装**：版本清单、版本 JSON、继承链合并、客户端、依赖库、资源索引与资源对象、日志配置下载；SHA-1 校验、断点续传、并发下载、失败重试。
- **四档下载源策略**：只官方 / 优先官方 / 优先镜像 / 只镜像，默认优先官方、其次 BMCLAPI。
- **Java 管理**：逐实例选择/自动选择 Java，显示兼容性状态；只检查最低版本、不限制最高版本；版本过低时弹窗提示，可逐实例关闭兼容性检验；没有 Java 时提示并支持从 Mojang、Adoptium、Zulu 或 Liberica 自动下载 Java 运行时（含 32/64 位与内存需求警告）；支持 G1 / ZGC / Aikar's flags 预设，并支持导入自定义 JSON JVM 预设。
- **EULA**：首次真实启动时弹窗要求同意 Minecraft EULA；同意后额外提示“多人联机功能不由启动器提供”的免责声明，并写入 `eula.txt`。
- **加载器安装**：Fabric（Meta profile JSON，强制安装 Fabric API，默认最新 API）、Forge（安装器 spec 0 直接解压，spec 1+ 运行 processors）、NeoForge（1.20.1 旧产物与 1.20.2+/26.x）与 Cleanroom（1.12.2，从官方安装器提取版本 JSON，Java 25）；支持选择加载器/Fabric API/OptiFine 版本并显示更新时间；安装失败自动清理本次文件并写入诊断 JSON。
- **OptiFine**：仅在 Forge 或 Cleanroom + 用户显式选择时安装，默认不安装，遵守分发限制；Fabric、NeoForge 与原版下禁用。
- **Mod 管理**：解析 `fabric.mod.json` / `mods.toml` / `neoforge.mods.toml` / `mcmod.info`；检测重复 Mod、缺失/禁用依赖、冲突与加载器不匹配；支持单个/批量启用禁用、导入、删除，WPF 与 CLI 均可操作。
- **整合包与资源包**：导入 Modrinth `.mrpack` 与 CurseForge `.zip`（完整下载 `files[]`、SHA-1 校验、解压 overrides）；导出为 Modrinth `.mrpack`；列出、导入、删除资源包与光影包。
- **CurseForge**：按实例游戏版本与加载器搜索、查看文件、一键安装（递归安装必需依赖），支持项目 ID / slug / 链接；API key 从环境变量或发布版构建元数据读取，缺省时明确提示。
- **真实启动**：检查文件完整性（大小 + SHA-1，含资源对象存在性）、解压 natives、按规则过滤库、构建命令并启动进程；支持 Fabric `arguments.jvm`、Forge/NeoForge `${library_directory}`/`--launchTarget` 与 Cleanroom `--tweakClass`；`arguments.jvm`/`arguments.game` 按 OS/架构/feature 规则过滤。
- **诊断**：解析 `crash-reports` / `latest.log`，按崩溃堆栈与 Mod 元数据排序可疑 Mod；导出包含日志、崩溃报告、安装诊断和系统信息的脱敏 zip，不包含账户文件。
- **下载控制**：安装与 Java 下载显示实时速度，支持暂停/继续；主题切换、Mod 删除与资源包区域已接入 WPF。
- 启动流水线：校验 → 读取版本 → 解析 Java → 检查文件 → 解压 natives → 检查 EULA → 构建命令 → 启动/干跑；失败可定位到具体步骤。
- CLI：无 GUI 环境下安装游戏、管理 Java/实例/账户/配置、Mod/整合包/资源包、CurseForge Mod，并预览启动命令。
- 单元测试：183 个测试覆盖下载源、HTTP 下载、Java 需求/选择/安装/第三方提供方/归档解压/平台校验/JVM 预设、配置、账户、Java 探测、EULA、实例、加载器目录、Forge/NeoForge 安装器解析、Cleanroom 安装器解析、库规则（含 `arch: x64`）、版本继承、arguments 规则/占位符、安装回滚与诊断、Mod 元数据/扫描/启停、整合包导入导出、CurseForge 客户端/安装器/依赖、资源包管理与安装/启动流水线。

## 尚未实现

- Microsoft 登录 / authlib-injector 第三方认证（v0.3，暂时跳过）。
- Quilt / Legacy Fabric。
- CurseForge 作者禁止第三方分发的文件无法下载；暂不做 Mod 更新检查与可选依赖选择界面。
- 首次启动向导（本阶段暂缓）。
- 资源包/光影包的“当前启用”选择（文件管理已完成，options.txt 选择器待接入）。
- 启动器自更新、安装包与签名发布。
- 托盘、完整 i18n 热切换、更多无障碍细节等体验项。

路线图见 [docs/roadmap.md](docs/roadmap.md)。

## 环境要求

- Windows 10 1809+ / Windows 11（WPF 运行）
- .NET 10 SDK（当前锁定 10.0.4xx）
- 可选：Java 8/17/21/25，用于启动 Minecraft

Linux/macOS 可以构建 Core、CLI 和测试；WPF 项目通过 `EnableWindowsTargeting` 可交叉编译，但无法运行。

## 构建与运行

```powershell
dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release --no-build
dotnet run --project src/ECL.App
```

CLI：

```powershell
dotnet run --project src/ECL.Cli -- version
dotnet run --project src/ECL.Cli -- account add-offline Steve --default
dotnet run --project src/ECL.Cli -- instance create "Survival" --version 1.21.1
dotnet run --project src/ECL.Cli -- instance create "Fabric" --version 1.21.1 --loader fabric
dotnet run --project src/ECL.Cli -- instance create "Forge" --version 1.20.1 --loader forge
dotnet run --project src/ECL.Cli -- instance create "NeoForge" --version 1.21.1 --loader neoforge
dotnet run --project src/ECL.Cli -- loader list --version 1.20.1 --type forge
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- java install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
dotnet run --project src/ECL.Cli -- crash <实例ID>
dotnet run --project src/ECL.Cli -- diagnostics export <实例ID>
dotnet run --project src/ECL.Cli -- mod list <实例ID>
dotnet run --project src/ECL.Cli -- mod check <实例ID>
dotnet run --project src/ECL.Cli -- mod import <实例ID> "C:\mods\example.jar"
dotnet run --project src/ECL.Cli -- modpack import <实例ID> "C:\packs\example.mrpack"
dotnet run --project src/ECL.Cli -- modpack import <实例ID> "C:\packs\example.zip"
dotnet run --project src/ECL.Cli -- curseforge status --check
dotnet run --project src/ECL.Cli -- curseforge search jei --game-version 1.20.1 --loader forge
dotnet run --project src/ECL.Cli -- curseforge install <实例ID> jei
dotnet run --project src/ECL.Cli -- pack list <实例ID>
dotnet run --project src/ECL.Cli -- settings set java.jvmPreset aikar
dotnet run --project src/ECL.Cli -- settings set java.runtimeProvider adoptium
dotnet run --project src/ECL.Cli -- settings list
```

使用 `--data-dir <目录>` 可以隔离数据目录；使用 `--lang zh-CN|en-US` 覆盖语言。

## 仓库结构

```
src/
  ECL.Core/            基础设施：路径、结果类型、JSON、日志、本地化
  ECL.Configuration/   元数据驱动配置：定义、注册表、作用域、持久化
  ECL.Download/        HTTP 下载、镜像映射、四档下载源策略、校验与续传
  ECL.Auth/            账户模型、离线 UUID、账户存储
  ECL.Java/            Java 版本解析、运行时探测、Java 需求/兼容性、运行时下载
  ECL.Minecraft/       实例、版本、安装器、Java 选择、Mod、CurseForge、诊断、启动流水线、进程启动
  ECL.App/             WPF 外壳（MVVM）
  ECL.Cli/             命令行入口
tests/
  ECL.Tests/           单元测试
docs/                  架构、配置、启动流程、仓库策略、路线图、ADR
build/                 构建、打包、签名脚本
```

## 数据目录

默认位于 `%LOCALAPPDATA%\EnderCraftLauncher`。完整布局见 [docs/architecture.md](docs/architecture.md)。

## 仓库拆分

- 本仓库 `Ender-Craft-Launcher`（public）：全部源码、文档、构建脚本与公开配置，GPL-3.0-or-later。
- `Ender-Craft-Launcher-Core`（private）：只放代码签名证书、私钥、OAuth/上传凭据等极私密材料。

**本仓库的构建不依赖 Core 仓库。** 签名材料通过环境变量从 Core 仓库注入；CurseForge 请求经私有代理转发，key 只保存在代理服务端，构建与客户端都不读取，详见 [docs/repository-policy.md](docs/repository-policy.md) 与 [docs/curseforge.md](docs/curseforge.md)。

## 文档

- [架构总览](docs/architecture.md)
- [配置系统](docs/configuration.md)
- [Java 管理](docs/java.md)
- [加载器与组件](docs/loaders.md)
- [Mod、整合包与资源包](docs/mods.md)
- [CurseForge 集成](docs/curseforge.md)
- [崩溃分析与诊断包](docs/diagnostics.md)
- [启动流水线](docs/launch-pipeline.md)
- [仓库与保密策略](docs/repository-policy.md)
- [路线图](docs/roadmap.md)
- [ADR](docs/adr)

## 许可证

GPL-3.0-or-later。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang 或 Microsoft 无关联。
