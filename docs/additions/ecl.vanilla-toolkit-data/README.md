# Vanilla Toolkit Data

- Addition ID: `ecl.vanilla-toolkit-data`
- Type: `Data`
- Category: Gameplay Data
- Registry Status: `Planned`
- Current Version: 尚未发布

## 概述

供 Vanilla Toolkit 使用的 Minecraft 原版版本化结构化数据包，与工具实现分离并可独立更新.

## 功能范围

- 提供可独立版本化、校验、更新和回滚的数据内容。\n- 声明数据来源、适用范围、Schema 版本、更新时间和验证状态。\n- 由对应 Feature Addition 或 ECL Core 的受控数据接口消费。

## 边界与限制

- 本条目登记的是 ECL 自主提供的能力；第三方产品仅可作为功能参照，不构成默认运行时依赖。
- 未发布前不代表可下载、可安装或已完成实现。
- Core API 兼容范围、依赖、权限、平台和 Minecraft/Loader 支持矩阵尚待实现与发布阶段确定，不以猜测值填充。
- 任何文件写入、外部访问或敏感数据处理均须遵循 ECL 对应的 Core、安全和用户授权边界。

## 依赖与权限

当前尚未发布。依赖和 Capability/Permission 清单将在实现与发布前按 ADR 0032、ADR 0053 确定；本登记本身不授予任何权限。

## 兼容性与发布

- Core API Compatibility：待定义
- Minecraft / Loader / Platform Compatibility：待验证
- Release Assets：暂无
- License / Data Sources：待审核或按实际数据来源逐项声明

## 相关文档

- [ADR 0040](../../adr/0040-vanilla-toolkit-addition.md)
- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)

## 修订记录

- 2026-11-07：依据现有 ADR 与维护者确认的产品方向建立登记条目；尚无正式发布资产。
