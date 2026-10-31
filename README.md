# Ender Craft Launcher

> **正式主线（main）** · 面向 Windows 的 Minecraft 启动器，使用 C# / .NET 10 / WPF 构建。

ECL 是一个以 Minecraft 实例为中心的启动器，提供游戏安装、启动、Java 管理和 Mod / 整合包管理能力。**main 是正式主线**：这里保留经过集成的产品基线，不承诺包含 dev 或 nightly 中尚未合并的功能。

本分支功能状态以本分支代码和对应 CI 为准。请勿将开发分支中的新功能视为 main 已经具备。

## 功能概览

- **实例管理**：创建、查看和管理独立 Minecraft 实例。
- **游戏安装**：支持 Minecraft 原版、Fabric、Forge、NeoForge 与 Cleanroom 的安装和启动。
- **Java 管理**：探测本机 Java、检查版本兼容性、下载 Java 运行时；支持实例级 Java 选择和 JVM 参数预设。
- **账户**：支持离线账户。Microsoft 正式账户认证与第三方 Yggdrasil 认证尚不属于此分支的已交付能力。
- **Mod 管理**：读取常见 Mod 元数据，检查重复项、依赖缺失、冲突和加载器不匹配；支持导入、启用、禁用和删除。
- **整合包**：导入 Modrinth .mrpack 与 CurseForge .zip，并支持导出 Modrinth 格式整合包。
- **资源管理**：管理资源包和光影包，并同步游戏配置中的启用状态。
- **内容来源**：支持 CurseForge 内容搜索与安装；受作者分发限制的文件仍须遵守来源平台规则。
- **启动与诊断**：执行启动前检查、版本解析、文件校验、原生库解压和进程启动；支持崩溃分析与脱敏诊断包。
- **命令行**：提供安装、实例、Java、账户、配置、Mod、整合包和启动相关操作。
- **自动化测试**：以本分支 GitHub Actions 的最终结果为准。

## 当前限制

- Microsoft 账户与 Authlib-Injector 第三方账户不在此分支的已交付范围内。
- Quilt、Legacy Fabric 等加载器尚未支持。
- Mod 更新检查、可选依赖选择和整合包原地更新尚未完成。
- 启动器安装包、自更新和正式代码签名发布尚未完成。
- 首次启动向导、托盘和更多无障碍体验仍待规划。
- 本分支的 UI 与功能布局可能早于 dev / nightly 的重构；请以当前程序实际呈现为准。

## 获取与构建

普通用户请优先使用 main 对应的正式发布产物。如果仓库尚未提供正式安装包，请不要把源码分支误认为已签名、已发布的安装程序。

环境要求：Windows 10 1809+ / Windows 11（运行 WPF）、.NET 10 SDK（以 global.json 为准）、以及目标 Minecraft 版本兼容的 Java 运行时。

```powershell
git clone https://github.com/hellokug2699/Ender-Craft-Launcher.git
cd Ender-Craft-Launcher
git switch main
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

## 分支说明

| 分支 | 用途 |
| --- | --- |
| main | 正式主线，优先保持可用性与稳定基线 |
| dev | 集成功能与候选变更，供合并前验证 |
| nightly | 高频开发分支，可能包含尚未稳定的新功能 |

- [查看 dev 分支](https://github.com/hellokug2699/Ender-Craft-Launcher/tree/dev)
- [查看 nightly 分支](https://github.com/hellokug2699/Ender-Craft-Launcher/tree/nightly)
- [提交问题与反馈](https://github.com/hellokug2699/Ender-Craft-Launcher/issues)

## 仓库与安全

公开仓库存放源码、文档、构建脚本和公开配置。私有仓库 Ender-Craft-Launcher-Core 仅用于存放不应公开的签名材料及其他敏感内容；公开仓库常规构建不应依赖私有仓库。

不要提交证书私钥、访问令牌、密码、API 密钥或其他秘密。OAuth Client ID 等公开应用标识符不等于 Client Secret。分享诊断信息前，请检查其中是否包含个人路径或其他隐私。

## 文档

- [架构总览](docs/architecture.md)
- [配置系统](docs/configuration.md)
- [Java 管理](docs/java.md)
- [加载器与组件](docs/loaders.md)
- [Mod、整合包与资源包](docs/mods.md)
- [CurseForge 集成](docs/curseforge.md)
- [崩溃分析与诊断包](docs/diagnostics.md)
- [启动流水线](docs/launch-pipeline.md)
- [开发路线图](docs/roadmap.md)

## 许可证

GPL-3.0-or-later，详见 [LICENSE](LICENSE)。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang Studios 或 Microsoft 无关联。
