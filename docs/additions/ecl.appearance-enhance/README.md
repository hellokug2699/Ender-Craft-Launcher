# Appearance Enhance

- Addition ID: `ecl.appearance-enhance`
- Type: `Feature`
- Category: Account Appearance
- Registry Status: `Planned`
- Current Version: 尚未发布

## 概述

提供 ECL 的完整账户外观增强体验，包括本地衣柜、3D 玩家模型查看和按账户类型/Provider 能力开放的外观管理。

Core 继续负责基础当前外观读取与原生 2D 皮肤展示；该 Addition 不得成为普通登录、主页展示或 Minecraft 启动的必要依赖。

## 功能范围

- 本地皮肤资源导入、校验、整理、收藏、预览和删除；
- Classic / Slim 玩家模型的 3D 查看、旋转与缩放；
- 按 Offline、Microsoft 和第三方 Yggdrasil 账户类型提供明确区分的外观能力；
- Microsoft 外观管理仅开放已逐项验证的操作；
- Offline 本地外观应用必须说明客户端、服务端或兼容组件依赖。

## 明确边界

- 第三方 Yggdrasil 账户仅消费只读当前外观；不提供衣柜关联、选择/应用、上传/替换或披风管理。
- 不复制账户登录、OAuth、令牌刷新或凭据存储实现；复用 Core 的账户身份、认证上下文和受控外观读取接口。
- Core 的基础读取与 2D 展示不得依赖本 Addition。
- 不建设自有皮肤托管、外观代理或用户资源云同步服务。
- 不将旧 Core Wardrobe UI/领域实现整体迁移或复用；旧代码仅供行为审计与底层组件逐项评估。
- 所有权限、资源访问、进程隔离和生命周期必须遵循 ADR 0032、0053、0054、0055。

## 依赖与权限

当前尚未进入实质开发。Core API、Capability/Permission、Addition Runtime 与 IPC 依赖待按 Accepted ADR 完成设计和验证。本登记不授予任何权限，也不代表已有可安装包。

## 兼容性与发布

- Core API Compatibility：待定义
- Minecraft / Loader / Platform Compatibility：待验证
- Release Assets：暂无
- License / Data Sources：待审核或按实际数据来源逐项声明
- Registry Status 保持 Planned，直到发布资产、Manifest、兼容性和安全要求均可验证；不得仅因源码或页面存在改为 Available。

## 相关文档

- [ADR 0070](../../adr/0070-account-appearance-and-appearance-enhance-addition-architecture.md)
- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)

## 修订记录

- 2026-11-15：按 ADR 0070 与维护者决定登记为 Planned；无正式发布资产，不可安装。
