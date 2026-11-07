# Resource Pack Studio

- Addition ID: `ecl.resource-pack-studio`
- Type: `Feature`
- Category: Resource Inspection and Preview
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

Resource Pack Studio 是 ECL 的可选资源包浏览、视觉预览与结构分析 Addition。它面向不启动 Minecraft、不将资源包安装到实例的预览场景，提供原始资源浏览、材质/模型/动画预览、资源状态变体展示和资源对照。

本条目目前处于 Proposed。登记候选不代表架构已批准、功能已实现或存在可下载资产。

## 功能范围

- 浏览 ZIP 资源包的原始目录和文件。
- 预览图片、材质及透明通道。
- 对受支持的标准资源格式解析并预览方块、物品等模型。
- 播放受支持的材质动画。
- 从资源定义发现并展示状态变体，包括红石线等多状态视觉资源。
- 查看资源引用、模型继承、缺失引用和解析诊断。
- 对照目标版本的原版资源或其他资源包中的对应资源。
- 对非标准资源组织结构进行尽力发现，并保留原始资源访问能力。

## 明确不包含

- Minecraft 世界或游戏逻辑模拟。
- 方块放置、破坏、编辑或场景搭建。
- 玩家移动、第一人称操作或世界漫游。
- 红石信号传播、方块更新或邻居更新模拟。
- 完整复刻 Minecraft 客户端或渲染管线。

预览交互仅用于观察资源，例如旋转、缩放、平移、切换展示视角、选择状态和控制动画播放。

## 架构边界

- 作为独立 Feature Addition 按需安装，不进入普通启动链。
- 专业资源解析、模型构建和渲染不进入 ECL 主进程。
- 遵循 ADR 0055 的 Addition Runtime、Workspace、Native UI Host 和 Resource Broker 边界。
- 复用 ECL Security Center / Core 提供的受控安全能力，不在 Addition 内复制归档安全检测系统。
- 优先复用目标 Minecraft 版本匹配的本地原版资源缓存。
- 标准格式解析必须关联目标版本；自定义结构尽力发现，Unsupported 不等于 Invalid。
- 按需解析和有界缓存，避免打开资源包时完整加载所有资源。

## 依赖与权限

当前尚未批准或发布。Core API 兼容范围、依赖和 Capability/Permission 清单将在架构决策获批及实现阶段确定；本登记本身不授予任何权限。

安全检测能力必须通过 ECL 受控接口使用。若适用于归档预览的检测接口尚未具备，应先补齐 Core 能力，不得在 Addition 内绕过安全中心或复制一套检测逻辑。

## 兼容性与发布

- Core API Compatibility：待定义
- Minecraft Resource Format Compatibility：待验证
- Platform / Rendering Compatibility：待验证
- Release Assets：暂无
- License / Data Sources：待审核或按实际依赖逐项声明

## 相关文档

- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0043](../../adr/0043-minecraft-threat-protection-and-security-analysis.md)
- [ADR 0054](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0058](../../adr/0058-resource-pack-studio-architecture.md)

## 修订记录

- 2026-11-07：建立 Proposed 登记候选；架构尚待评审，无正式发布资产。
