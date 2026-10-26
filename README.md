# Ender Craft Launcher

> 一个面向 Windows 的 Minecraft 启动器，使用 C# / .NET 10 / WPF 构建。

ECL 的目标是达到 HMCL、PCL2 级别的完整度，同时在配置系统、启动流程和交互上做出自己的设计。当前仓库处于 **v0.4 Java 管理** 阶段（v0.3 账户体系因 Microsoft OAuth 审核限制暂时跳过）：能够安装原版、下载并选择 Java 运行时、自动检测 Java 与游戏版本的兼容性，并完成真实启动。

## 现在能做什么

- WPF 桌面壳：实例库、设置、关于三个页面，深色主题。
- 多实例管理：创建/列出/删除实例，每个实例有独立目录与元数据。
- 离线账户：按 Java `UUID.nameUUIDFromBytes` 规则生成 UUID，可设默认账户。
- 元数据驱动配置：全局设置与实例覆盖，自动生成设置页，支持校验、可见性依赖、来源标记。
- **原版安装**：版本清单、版本 JSON、继承链合并、客户端、依赖库、资源索引与资源对象、日志配置下载；SHA-1 校验、断点续传、并发下载、失败重试。
- **四档下载源策略**：只官方 / 优先官方 / 优先镜像 / 只镜像，默认优先官方、其次 BMCLAPI。
- **Java 管理**：逐实例选择/自动选择 Java，显示兼容性状态；版本不匹配时弹窗提示，可逐实例关闭兼容性检验；没有 Java 时提示并支持从 Mojang 自动下载 Java 运行时到启动器目录。
- **真实启动**：检查文件完整性、解压 natives、按规则过滤库、写入 EULA、构建命令并启动进程。
- 启动流水线：校验 → 读取版本 → 解析 Java → 检查文件 → 解压 natives → 检查 EULA → 构建命令 → 启动/干跑；失败可定位到具体步骤。
- CLI：无 GUI 环境下安装游戏、管理 Java/实例/账户/配置并预览启动命令。
- 单元测试：61 个测试覆盖下载源、HTTP 下载、Java 需求/选择/安装、配置、账户、Java 探测、实例、库规则、版本继承、安装与启动流水线。

## 尚未实现

- Microsoft 登录 / authlib-injector 第三方认证（v0.3，暂时跳过）。
- Forge / NeoForge / Fabric / Quilt / OptiFine 安装。
- Mod、整合包、资源包、光影管理。
- JVM 参数预设（G1/ZGC/Aikar flags）。
- 启动器自更新、崩溃分析、安装包与签名发布。
- 浅色主题、托盘、完整 i18n 覆盖等体验项。

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
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- java install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
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
  ECL.Minecraft/       实例、版本、安装器、Java 选择、启动流水线、进程启动
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

**本仓库的构建不依赖 Core 仓库。** 签名时通过环境变量从 Core 仓库提供证书，详见 [docs/repository-policy.md](docs/repository-policy.md)。

## 文档

- [架构总览](docs/architecture.md)
- [配置系统](docs/configuration.md)
- [Java 管理](docs/java.md)
- [启动流水线](docs/launch-pipeline.md)
- [仓库与保密策略](docs/repository-policy.md)
- [路线图](docs/roadmap.md)
- [ADR](docs/adr)

## 许可证

GPL-3.0-or-later。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang 或 Microsoft 无关联。
