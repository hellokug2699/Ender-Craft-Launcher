# ECL Music Player

- Addition ID: `ecl.music-player`
- Type: `Feature`
- Category: Music and Audio
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL Music Player 是 ECL 的可选本地音乐播放 Addition，面向玩家在 Minecraft 游戏过程中播放自己提供的背景音乐，并在需要仔细听取游戏声音时快速暂停外部音乐。

播放器由 ECL Core 托管音频播放服务；Addition 提供播放器工作区、音乐库、播放列表和播放控制体验。Minecraft 游戏内音量控制由随 Addition 提供的 ECL Audio Bridge Java Agent 实现。

本条目当前为 Proposed。登记不代表已经实现、通过兼容性验证或存在可下载资产。

## 功能方向

- 使用用户自行提供的本地音乐文件；ECL 不分发音乐曲库。
- 管理音乐库索引、元数据和播放列表；不擅自修改或转码原始音乐文件。
- 通过 ECL Core 托管的音频服务播放、暂停、继续和停止音乐。
- 提供可在播放器工作区之外触发的快速暂停控制，便于玩家临时仔细听取游戏声音。
- 在用户明确授权并且目标实例兼容时，通过 ECL Audio Bridge 控制 Minecraft 游戏内 `MUSIC` 分类音量。
- 支持与 Minecraft 实例会话绑定的控制；多个游戏实例不得互相误控。

## 音频控制行为

| ECL Music Player 状态 | Minecraft `MUSIC` 分类音量 |
|---|---:|
| ECL 音乐正在播放 | `0.0` |
| ECL 音乐暂停 | `1.0` |
| ECL 音乐停止或释放控制 | `1.0`，随后执行会话清理 |

只控制 `MUSIC` 分类，不主动修改唱片机 `RECORDS` 或其他游戏音量分类。模组若使用其他声音分类播放音乐，不保证被此桥接静音。

## ECL Audio Bridge

- Audio Bridge 是本 Addition 随附的独立 Java Agent 运行时组件，不是单独的 Addition，也不是必须安装的 Fabric/Forge/NeoForge Mod。
- ECL Core 继续使用现有技术栈，只负责在用户授权和选择目标实例后注入启动参数并通过受控本地通信发送最小音量控制指令。
- Agent 在目标 Minecraft JVM 内运行，负责客户端对象定位、版本适配和客户端线程调度；不负责音乐播放、解码、音乐库扫描或 UI。
- 不同 Minecraft 版本可以使用不同 Adapter；Loader 环境必须逐项实机验证。映射/Javadoc 查证不等于兼容性通过。
- Agent 在 Minecraft 进程内执行，具有目标进程相同的操作系统用户权限；它不是完整 OS 沙箱。其预期用途虽限于游戏音量控制，但不能宣称技术上只能访问音量。
- 不支持或未授权时，播放器仍可使用 Core 音频服务；游戏内 BGM 自动控制不可用。

## 进程修改授权与重要提示

启用游戏内音量集成需要将 Java Agent 注入用户选择的 Minecraft JVM。这属于实际修改 Minecraft 进程，必须遵循 ADR 0053 的能力授权流程。

权限授权弹窗必须以独立、不可折叠且高显著性的警示区域，完整显示以下原文；不得删减、改写、弱化或仅放在二级详情中：

> **此扩展会更改Minecraft进程**  
> **ECL 音乐播放器依靠Java Agent更改游戏内音量，以避免多音源冲突**

该警示必须在用户作出授权决定前完整可见。用户拒绝或撤销授权后，ECL 不得向目标实例注入 Agent；本地音乐播放仍可使用，但 Minecraft BGM 不会自动静音/恢复，可能与 ECL 音乐同时播放。

Agent 包必须经过来源、签名/完整性和兼容性检查。授权不覆盖验证失败。Agent 不得被描述为 OS 沙箱，也不得获得通用代码执行或任意资源访问接口。

## 依赖与权限

- Required Additions：无。
- Optional Additions：无。
- Core Audio Service：由 ECL Core 提供；具体 Core API 兼容范围待定义。
- Minecraft Process Modification：需要用户明确授权；能力 ID 与 Manifest 权限 Schema 待实现评审后定义。
- 本登记不自动授予进程修改权限，也不代表任何实例已获授权。

## 兼容性与发布

- Minecraft Version Compatibility：待验证。
- Loader Compatibility：Vanilla / Fabric / Forge / NeoForge 均须按具体版本实机验证；不作全范围兼容承诺。
- 初始原型验证目标：Minecraft 1.7.10、1.12.2、1.16.5、1.19.2、1.19.3、1.20.1、1.21.1、1.21.11；这些是测试候选，不是已支持版本列表。
- Java Runtime Compatibility：Agent 的字节码目标与依赖须覆盖旧版实例的 Java 运行环境，待原型验证。
- Platform Compatibility：待验证。
- Release Assets：暂无。
- License：代码许可及音频解码依赖许可须在发布前审核。

## 相关文档

- [ADR 0067：ECL Music Player and Minecraft Audio Bridge Architecture](../../adr/0067-ecl-music-player-and-minecraft-audio-bridge-architecture.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0039：Application Runtime Lifecycle and Graceful Shutdown](../../adr/0039-application-runtime-lifecycle-and-graceful-shutdown.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055：ECL 混合工作区与原生工具平台架构](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)

## 修订记录

- 2026-11-09：建立 Proposed 登记条目；明确 Core 托管音频播放、随 Addition 提供的 Java Agent、游戏内 `MUSIC` 分类控制、进程修改授权警示及待验证兼容性范围。无正式发布资产。