# Shader Studio

- Addition ID: `ecl.shader-studio`
- Type: `Feature`
- Category: Shader Inspection and Preview
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

Shader Studio 是 ECL 的可选光影包浏览、结构分析、兼容性信息呈现与场景预览 Addition。它计划提供由 ECL 统一维护的固定测试环境，帮助用户在可重复的场景与取景条件下观察不同光影包的表现。

本条目目前处于 Proposed。登记候选不代表架构已批准、功能已实现或存在可下载资产。

## 规划功能

- 浏览光影包归档、目录和配置内容。
- 对受支持的 Shader 源文件和配置进行静态分析。
- 展示光影包依赖、渲染 Pass、Loader 与版本兼容性信息（具体覆盖待验证）。
- 在受支持的渲染环境中运行光影包并进行场景预览。
- 提供固定、可恢复的官方标准测试场景和标准取景。
- 标准场景至少包含河流/水体、草地、树林和远处山脉等代表性元素。
- 同一地点规划日出、白天、黄昏和黑夜的对应取景。
- 逐步扩展更多原版群系的官方场景。

## 场景交付原则

- 标准场景与高级场景均随同一个 Shader Studio Addition 统一交付。
- 不拆分为收费包、独立付费 Addition 或额外权益包。
- 用户安装 Addition 后，所有随当前版本发布且兼容的官方场景均可使用。
- Standard / Advanced 仅用于内部资产制作与维护规划，不构成用户等级、权限或功能差异。
- 官方场景强调固定布局、稳定身份、可恢复取景和可重复测试条件。
- BOP 等第三方模组群系不属于首期官方场景承诺；未来是否支持需单独评估资产来源、许可和适配方式。

## 架构边界

- 作为独立 Feature Addition 按需安装，不进入普通启动链。
- 光影包分析、编译和预览业务代码不进入 ECL 主进程。
- 场景资产、渲染环境、测试配置和预览控制能力应保持可区分。
- 固定测试场景不等于完整 Minecraft 世界模拟器，也不保证兼容所有 Shader Loader 或光影包。
- 静态分析、语法/编译检查、Loader 兼容和实际渲染预览必须区分表达。
- 遵循 ADR 0055 的 Addition Runtime、Workspace、Native UI Host、IPC 和 Resource Broker 边界。
- 外部内容处理、编译和 GPU 执行必须考虑权限、资源预算、取消、失败恢复和资源释放。
- 进程隔离不等于 GPU 沙箱或完整 OS 沙箱。

## 依赖与权限

当前尚未批准或发布。Core API 兼容范围、依赖和 Capability/Permission 清单将在架构决策获批及实现阶段确定；本登记本身不授予任何权限。

## 兼容性与发布

- Core API Compatibility：待定义
- Shader Loader / Minecraft Compatibility：待验证
- Graphics API / GPU Compatibility：待验证
- Release Assets：暂无
- Scene Asset Sources / Licenses：待审核
- Official Scene Catalog：待制作与验收

## 相关文档

- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0060](../../adr/0060-multi-addition-visual-preview-composition.md)
- [ADR 0059](../../adr/0059-shader-studio-and-standard-scene-environment-architecture.md)

## 修订记录

- 2026-11-08：建立 Proposed 登记候选；场景与渲染架构尚待评审，无正式发布资产。
