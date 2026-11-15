# Map Art Studio

- Addition ID: `ecl.map-art-studio`
- Type: `Feature`
- Category: Map Art Planning and Material Analysis
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

Map Art Studio 是 ECL 的可选地图画规划与材料分析 Addition。它面向从图片创建 Minecraft 地图画、整理已有地图数据、规划单张或多地图拼接画布，以及分析颜色映射和材料需求等场景。

本条目目前处于 Proposed。登记候选不代表架构已批准、功能已实现或存在可下载资产。

## 功能方向

- 从图片创建地图画画布，并根据用户指定的尺寸生成候选方案。
- 使用统一画布模型支持单张地图和多地图拼接。
- 导入受支持的 Minecraft 原生地图数据，还原其实际保存的像素颜色及可读取元数据。
- 对地图颜色进行版本与环境相关的分析，展示材料候选和不确定性。
- 比较直接颜色映射、渐变抖动和自适应映射等策略。
- 根据目标尺寸、视觉质量目标和材料约束进行方案比较。
- 汇总整幅作品的材料需求，并支持按地图区域、颜色和材料类型拆分。
- 在支持且经过验证的范围内，将地图画方案交给 Schematic Preview 进行可选的三维结构预览。
- 将材料清单按用户授权导出或交给 Player Workspace 管理。

## 关键边界

- 地图数据反向导入首先是像素还原，不承诺恢复原始图片、原始方块、材料或建造过程。
- 地图颜色不是方块身份的唯一编码；候选材料不得伪装成确定事实。
- 原生地图数据与屏幕截图属于不同输入来源，必须分别处理证据和误差。
- 光影和材质包通常影响屏幕渲染外观，不应被误认为原生地图数据颜色的直接来源；生物群系、版本和模组地图颜色行为则可能影响映射。
- 单张地图是统一多地图画布的一个特例；不得把地图数量、地图缩放级别、像素分辨率和建造结构尺寸混为一谈。
- 尺寸、颜色表现、细节保留和材料成本需要联合评估，不保证尺寸越大就必然更优。
- Map Art Studio 不模拟完整 Minecraft 世界，不承诺所有版本、模组或环境均可精确复现。

## 架构边界

- 作为独立 Feature Addition 按需安装，不进入普通游戏启动链。
- 不依赖 Schematic Preview、Resource Pack Studio、Shader Studio 或 Player Workspace 才能完成基本地图画规划。
- 与其他 Addition 的组合遵循 ADR 0053、ADR 0054、ADR 0055 和 ADR 0060；不得直接 IPC、共享私有对象或绕过 Core。
- 图片、地图文件和其他导入内容均视为不可信输入，遵循宿主资源访问和安全检查边界。
- 对大型画布、批量地图、候选映射和预览任务采用按需处理与有界资源预算。

## 依赖与权限

当前尚未批准或发布。Core API 兼容范围、依赖和 Capability/Permission 清单将在架构决策获批及实现阶段确定；本登记本身不授予任何权限。

## 兼容性与发布

- Core API Compatibility：待定义
- Minecraft Map Data Compatibility：待验证
- Minecraft Version / Modded Map Color Compatibility：待验证
- Image Import Compatibility：待定义
- Platform Compatibility：待验证
- Release Assets：暂无
- License / Data Sources：待审核或按实际依赖逐项声明

## 相关文档

- [ADR 0032](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0060](../../adr/0060-multi-addition-visual-preview-composition.md)
- [ADR 0064](../../adr/0064-map-art-studio-architecture.md)

## 修订记录

- 2026-11-08：建立 Proposed 登记候选；明确统一画布、单张/多地图、图片与原生地图数据导入、反向识别边界及可选投影预览集成。无正式发布资产。
