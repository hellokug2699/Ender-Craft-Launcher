# Ender Craft Launcher（ECL）

> 面向 Windows 的 Minecraft 启动器，使用 C# / .NET 10 / WPF 构建。

ECL 是一个以 Minecraft 实例为中心的启动器，提供游戏安装、启动、Java 管理、账户、Mod / 整合包 / 资源管理以及 CLI 能力。

## 功能概览

- **实例管理**：创建、查看、导入和管理独立 Minecraft 实例。
- **游戏与加载器**：支持 Minecraft 原版、Fabric、Forge、NeoForge 与 Cleanroom 的安装和启动。
- **Java 管理**：探测本机 Java、检查版本兼容性、下载 Java 运行时，并支持实例级 Java 选择和 JVM 参数预设。
- **账户**：支持离线账户；其他认证能力以当前分支实际代码和测试结果为准。
- **Mod 与内容**：管理 Mod、资源包、光影包和数据包，并检查常见元数据问题。
- **整合包**：支持 Modrinth `.mrpack` 与 CurseForge `.zip` 导入，并支持 Modrinth 格式导出。
- **内容平台**：支持 CurseForge / Modrinth 内容搜索与安装；受来源平台作者分发限制的文件仍须遵守平台规则。
- **启动与诊断**：执行启动前检查、版本解析、文件校验、原生库解压和进程启动，并提供崩溃分析与脱敏诊断包。
- **CLI 与测试**：提供无图形命令行入口，并持续建设自动化测试与 CI。

> 具体功能是否已经进入某个分支，请以该分支的代码、测试和 CI 为准；README 不记录分支专属功能状态。

## 分支工作流

```text
nightly  ──持续开发──>  dev  ──集成验证与验收──>  main
```

| 分支 | 定位 |
| --- | --- |
| `nightly` | 高频开发、架构调整和新功能首先集成的分支，允许尚未稳定的变化。 |
| `dev` | 集成验证与候选基线，用于确认跨模块协作、回归和 CI。 |
| `main` | 正式稳定基线，优先保持可用性与发布质量。 |

分支的详细规则、合并策略和文档约定见 [分支与合并策略](docs/branches.md)。

## 获取与构建

```powershell
git clone https://github.com/hellokug2699/Ender-Craft-Launcher.git
cd Ender-Craft-Launcher
# 根据需要切换到 main / dev / nightly
git switch nightly

dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
dotnet run --project src/ECL.App
```

CLI 示例：

```powershell
dotnet run --project src/ECL.Cli -- version
dotnet run --project src/ECL.Cli -- instance create "Survival" --version 1.21.1
dotnet run --project src/ECL.Cli -- install <实例ID>
dotnet run --project src/ECL.Cli -- launch <实例ID> --dry-run
```

环境要求：

- Windows 10 1809+ / Windows 11：运行 WPF 桌面程序。
- .NET 10 SDK：具体版本以仓库 `global.json` 为准。
- Java：按目标 Minecraft 版本选择兼容运行时，也可由 ECL 下载。
- Linux / macOS：可用于构建和测试 Core、CLI 等非 WPF 项目。

## 文档

- [架构总览](docs/architecture.md)
- [配置系统](docs/configuration.md)
- [认证说明](docs/authentication.md)
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

## 仓库与安全

公开仓库存放源码、文档、构建脚本和公开配置。私有仓库 `Ender-Craft-Launcher-Core` 用于不应公开的签名材料及其他敏感内容；公开仓库常规构建不应依赖私有仓库。

不要提交证书私钥、访问令牌、密码、API 密钥或其他秘密。OAuth Client ID 等公开应用标识符不等于 Client Secret。分享诊断信息前，请检查其中是否包含个人路径或其他隐私。

## 反馈

提交 Issue 时请尽量提供 ECL 构建版本、Windows / Java / Minecraft / 加载器版本、复现步骤以及脱敏后的日志或诊断包。

## 许可证

GPL-3.0-or-later，详见 [LICENSE](LICENSE)。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang Studios 或 Microsoft 无关联。
