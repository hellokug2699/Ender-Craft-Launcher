# GitHub Project Center

- Addition ID: `ecl.github-project-center`
- Type: `Feature`
- Category: Project Collaboration
- Registry Status: `In Development`
- Current Version: 尚未发布

## 概述

在 ECL 原生 UI 中查看指定项目仓库的 Releases、Issues、Pull Requests、Discussions 等协作信息，并提供受控反馈能力.

## 功能范围

- 通过 ECL 原生页面和受控 Core API 提供可选领域能力。\n- 按需安装和使用，不进入普通启动链。\n- 具体交互、支持范围和验收条件由对应 Feature Spec 细化。

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

- [ADR 0049](../../adr/0049-native-github-integration-and-repository-bounded-navigation.md)
- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)

## 修订记录

- 2026-11-07：依据现有 ADR 与维护者确认的产品方向建立登记条目；尚无正式发布资产。
