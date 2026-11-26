# ECL Browser

- Addition ID: `ecl.browser`
- Type: `Feature`
- Category: Browser & Web Protection
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL Browser 是由 ECL 安装、更新和管理的通用网页浏览器 Addition。它提供正常互联网浏览能力，并以 Chromium 运行时、独立 Browser Host、Profile 隔离、宿主权限控制和本地优先内容保护构建安全边界。

Minecraft 知识获取是重要使用场景，但不是浏览范围限制。浏览器不采用固定 Minecraft 站点白名单作为安全边界。

## 功能范围

- 多标签浏览、地址栏、搜索、导航、书签、历史记录和下载管理。
- 持久化 Profile、临时会话及多个独立 Profile。
- 由宿主管理的站点权限、弹窗、下载和外部协议控制。
- 广告过滤、隐私过滤、烦扰过滤和恶意网站防护。
- 本地拦截解释页面、规则来源说明及用户规则管理。
- 在 ECL 内访问 Minecraft Wiki、模组平台、视频站、社区、文档和其他正常互联网内容。

具体首期范围和验收条件以 [ADR 0073](../../adr/0073-ecl-browser-platform-and-protection-architecture.md) 及后续 Feature Spec 为准。

## 架构边界

- Browser 是独立 Feature Addition，不是 ECL Core 的原生浏览器基础设施。
- 浏览器外壳、入口和未安装提示由 ECL 原生 UI 提供；Browser Addition 提供可选浏览执行能力。
- Browser Host 与 ECL 主 UI 分离；网页 Renderer 不得直接获得 ECL IPC 或任意宿主能力。
- Chromium Runtime 必须保留适用的操作系统沙箱；不得以关闭 Sandbox 作为兼容性绕过。
- CEF/Chromium 具体版本、封装方式、Sandbox Bootstrap、WPF 窗口托管方式和过滤引擎均未完成实机 PoC，不视为已定案。
- 广告/隐私/烦扰过滤与恶意威胁情报必须区分；广告命中不等于网站恶意。
- 网络过滤优先在本地执行；不默认将完整 URL、查询参数、页面内容、Cookie 或浏览历史发送给远程服务。
- 在线 Threat Intelligence 是可选增强，不应成为基本浏览能力的前置条件。
- 不默认使用远程代理或 HTTPS 中间人解密。

## 依赖与权限

- Addition 依赖：当前没有声明其他 Addition 依赖。
- Core 宿主要求：需要 ECL 提供受控 Addition Runtime、版本化宿主能力接口及浏览器生命周期管理；具体 API 兼容范围待定义。
- Capability / Permission：待定义。网络请求、下载写入、站点权限、外部协议和 Profile 数据访问必须按 ADR 0053 的声明、授予与运行时执行模型逐项审查。
- 本登记不授予任何权限，也不代表相关宿主 API 已实现。
- Threat Intelligence Provider 不作为必需 Addition 依赖；未来若威胁情报数据具备独立分发和生命周期，再评估是否登记 Data Addition。

## 兼容性与发布

- Core API Compatibility：待定义
- Chromium Runtime / Windows Sandbox：待 PoC 验证
- WPF Host / DPI / IME / 视频与 GPU：待实机验证
- Release Assets：暂无
- License / Data Sources：待审核；过滤规则列表、Runtime 和第三方组件需分别记录许可证与分发条件。

## 相关文档

- [ADR 0073：ECL Browser Platform and Protection Architecture](../../adr/0073-ecl-browser-platform-and-protection-architecture.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0043：Minecraft 威胁防护与安全分析架构](../../adr/0043-minecraft-threat-protection-and-security-analysis.md)

## 修订记录

- 2026-11-26：建立 Proposed 登记。明确通用浏览器 Addition 定位；运行时、权限、兼容性和发布资产均未定案或验证。
