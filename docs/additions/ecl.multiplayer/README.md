# ECL Multiplayer

- Addition ID: `ecl.multiplayer`
- Type: `Feature`
- Category: Multiplayer
- Registry Status: `Planned`
- Current Version: 尚未发布

## 概述

ECL 自研的私人 Minecraft 联机体验与会话管理能力。目标是在 ECL 内提供创建会话、生成邀请、加入会话、联机环境检查、模组差异处理、连接状态管理和结束清理的完整流程。

本 Addition 不提供公共联机大厅、公开房间目录或自动匹配服务。

## 功能方向

- 私人会话：由房主主动创建会话并分享限时邀请，加入者通过邀请进入指定会话。
- 会话管理：呈现创建、网络建立、等待加入、环境检查、连接中、已连接、中断/恢复和结束等状态。
- 环境兼容检查：比较 Minecraft 版本、Loader 及可识别的模组清单，明确显示无法验证的信息。
- 模组差异处理：区分可从可信来源获取、可识别但无可信来源、无法识别、版本不匹配和本机额外模组等情况。
- 安全协作：将文件风险分析交由 ECL Security Center，不以哈希一致或房主提供文件作为安全证明。
- 可替换网络层：ECL 定义自己的会话模型，底层网络能力通过适配层接入。

## 架构与安全边界

- EasyTier 是底层网络能力候选，尚未完成许可、分发、平台兼容、进程生命周期与安全评估，不视为已确定依赖。
- Multiplayer 不负责自行判定文件恶意性；Security Center 负责扫描与安全策略。
- SHA-256 仅用于完整性/一致性校验，不证明来源可信或文件安全。
- 未知模组既不能被直接断言为恶意，也不能被宣称安全；扫描无法证明绝对安全。
- 房主提供的清单、文件名、版本和下载地址均按不可信输入处理。
- 不自动上传世界、实例、日志、账户令牌或本地诊断数据。
- 任何模组或实例变更必须经用户确认，并提供可恢复路径。

## 依赖与权限

当前没有正式运行时依赖或已授予权限。网络传输组件、Core API、Capability/Permission 清单和具体平台范围均待技术验证与评审。本登记本身不授予任何权限。

## 兼容性与发布

- Core API Compatibility：待定义
- Minecraft / Loader / Platform Compatibility：待验证
- Release Assets：暂无
- License / Data Sources：待审核
- Feature Spec：待建立

## 相关文档

- [ADR 0057: ECL Multiplayer Session Architecture](../../adr/0057-ecl-multiplayer-session-architecture.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0043：Minecraft 威胁防护与安全分析架构](../../adr/0043-minecraft-threat-protection-and-security-analysis.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)

## 修订记录

- 2026-11-07：建立 Planned 登记；ADR 0057 为 Proposed，尚无实现、正式依赖或发布资产。
