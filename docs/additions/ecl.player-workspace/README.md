# ECL Player Workspace

- Addition ID: `ecl.player-workspace`
- Type: `Feature`
- Category: Productivity & Organization
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL Player Workspace 是面向玩家的通用个人工作空间，用于整理资料、管理项目、记录目标与任务、维护表格和组织个人事务。

它不限定玩家整理的内容必须与 Minecraft 或任何游戏有关。玩家可以用它管理建筑计划、游戏攻略、学习计划、日常待办、个人项目或任何自己希望整理的资料。Minecraft 是重要的使用场景，但不是产品边界。

Player Workspace 以一个完整 Addition 提供连贯体验。表格、目标与任务、笔记、项目组织、导入导出等是内部能力方向，不要求玩家分别安装多个扩展。

## 目标能力

### Tables & Structured Data

- 创建和整理玩家自定义表格及字段。
- 支持常见的数据整理能力，例如分类、筛选和汇总。
- 将 CSV、TSV 作为通用数据交换格式的基础支持目标。
- 为投影材料清单等专用格式提供可扩展的适配方向。
- 对格式无法表达的数据明确说明导入导出损失，不静默丢弃。

### Goals & Tasks

- 管理玩家自定义目标、任务和子任务。
- 按需将任务关联到目标、项目、表格或资料。
- 支持简单待办清单，不强制采用复杂项目管理模型。
- 自动进度或状态联动必须可解释，并由明确产品行为和玩家控制决定。

### Projects & Notes

- 组织项目、集合、分组、笔记、参考资料和附件。
- 允许玩家自主决定组织结构，不强制 Minecraft 实例或游戏目录。
- 支持模板和可复用组织结构的后续扩展。

### Import, Export & Portability

- 提供通用数据导入导出能力。
- 通过可扩展适配能力支持游戏专用格式；不绑定单一投影模组或导出版本。
- 对未知格式给出明确的不支持反馈，不将无法识别等同于文件损坏。
- 为原生资料提供合理的导出、备份和迁移路径。

## 产品原则

- **用途不设限：** 不因资料与 Minecraft 无关而限制创建、保存、导入、导出或组织。
- **玩家自主组织：** 不强制固定目录、项目层级或任务管理方式。
- **本地优先：** 基本资料管理不要求云服务；不默认上传资料或附件。
- **数据可迁移：** 不以封闭格式锁定玩家；卸载或更新 Addition 不得擅自删除玩家资料。
- **集成可选：** Minecraft 和其他 ECL 功能只提供增强，不成为基本使用前提。
- **权限明确：** 玩家拥有数据不意味着其他 Addition 或 AI 自动获得读取和传输权限。

## 与 ECL 其他能力的关系

未来可以按需与 Schematic Preview、Multiplayer、ECL AI 等能力协作，例如导入材料清单、关联多人项目或在用户授权后辅助整理资料。

这些能力不是 Player Workspace 的必需依赖。跨 Addition 访问玩家资料必须经过 ECL Core 的资源中介和权限机制。允许本地读取不等于允许将资料发送给远程模型。

## 依赖与权限

- Required Additions：无。
- Optional Additions：当前未声明。
- Core API Compatibility：待定义。
- Capability / Permission：待实现评审与登记；本条目不授予任何权限。
- 文件访问、导入导出、附件关联及跨 Addition 数据访问必须遵循 ADR 0053 和宿主资源访问机制。

## 兼容性与发布

- Minecraft / Loader Compatibility：基本工作空间能力不要求 Minecraft 或 Loader 环境；专用格式适配按实际验证结果声明。
- Platform Compatibility：待定义与验证。
- Release Assets：暂无。
- License / Data Sources：代码许可、导入格式规范及相关数据来源须在发布前审核。
- Registry Status 为 Proposed；登记不代表已有可安装包或正式发布承诺。

## 相关文档

- [ADR 0063: ECL Player Workspace Architecture](../../adr/0063-ecl-player-workspace-architecture.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055：ECL 混合工作区与原生工具平台架构](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ECL Additions Registry](../README.md)

## 修订记录

- 2026-11-08：依据 ADR 0063 建立 ECL Player Workspace 登记条目；状态为 Proposed，尚无正式发布资产，不可安装。
