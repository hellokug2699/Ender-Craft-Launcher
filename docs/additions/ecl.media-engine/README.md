# ECL Media Engine

- Addition ID: `ecl.media-engine`
- Type: `Feature`
- Category: Media Processing
- Registry Status: `Proposed`
- Current Version: 尚未发布

## 概述

ECL Media Engine 是独立、可选的通用媒体处理 Addition，为用户和其他 Addition 提供普通图片、音频、视频格式识别、元数据读取及格式转换能力。

Media Engine 自身负责媒体能力发现、任务协调和处理后端治理；具体编解码能力由经过许可、安全和兼容性审查的后端提供。它不属于 ECL Core 的启动依赖，也不进入 Minecraft 普通启动链。

本条目当前为 Proposed。登记不代表已经实现、通过格式兼容性验证或存在可下载资产。

## 功能范围

- 常见图片格式的识别、读取、元数据提取和格式转换。
- 常见音频格式的识别、读取、元数据提取和格式转换。
- 常见视频容器与编码组合的识别、读取、元数据提取和格式转换。
- 明确呈现源格式、目标格式、编码参数以及可能的质量损失。
- 允许用户选择输出位置；默认保留源文件，不静默覆盖。
- 对未知、不支持、损坏、截断或部分支持的文件给出准确状态。

具体格式和编码组合以经过验证的后端能力为准，不承诺全格式兼容。

## 后端与依赖策略

- ECL 自主维护统一媒体能力抽象、后端治理和任务协调，不从零重写通用编解码器。
- 不将 FFmpeg 或任何单一媒体框架设为强制依赖或架构核心。
- FFmpeg 仅是未来可独立评估的可选后端候选；当前不批准默认捆绑或强制依赖。
- 可评估成熟第三方库、平台原生媒体 API 或与 ECL 当前技术栈兼容的实现。
- 后端必须声明格式能力、平台范围、依赖、许可证和限制；发布前完成代码、编解码器和分发义务审查。
- 不为单一功能无必要地引入额外语言运行时。确需原生库、独立进程或其他语言组件时，必须评估部署、调试、签名、升级和隔离成本。
- 图片、音频和视频后端应按需交付/加载，避免仅使用某一类能力时下载全部媒体依赖。

## 安全与隐私边界

- 媒体文件及元数据均视为不可信输入。
- 通过 Core 授权的资源访问路径读取和写入文件；处理过程受资源预算、取消和临时文件清理约束。
- 不得默认上传媒体内容、元数据或缩略图。
- 不静默安装系统级编解码器，不修改系统媒体关联或用户环境。
- 后端若运行原生代码或外部进程，必须如实说明实际权限边界；普通进程隔离不等于完整 OS 沙箱。

## 不支持的平台保护措施绕过

Media Engine 面向普通公开格式和已获得合法处理授权的媒体内容：

- 不开发、内置、捆绑或自动下载用于绕过第三方平台 DRM、访问控制或技术保护措施的专用解密器。
- 不将 NCM 等平台专有加密封装解密视为普通格式转换能力。
- 第三方开源许可证不替代内容权利人或平台对技术保护措施的授权。
- 不支持的受保护格式明确报告 Unsupported / Not Supported，不尝试静默绕过。
- 不因文件无法解析就推断用户文件违法，也不妨碍用户导入普通标准媒体文件。

## 与其他 Additions 的关系

- ECL Music Player 可在未来选择性复用音频识别、解码或转换能力；基本音乐播放不得强制依赖完整 Media Engine。
- Resource Pack Studio、Map Art Studio、Shader Studio 等可按需复用图片或视频能力，其领域语义仍由各自 Addition 负责。
- Addition 间协作遵循 Core 受控机制，不直接 IPC 或共享内部对象。
- Required Additions：无。
- Optional Additions：无（未来消费者关系须单独登记，不代表当前存在运行时依赖）。

## 兼容性与发布

- Image / Audio / Video Format Compatibility：待后端原型和逐项验证。
- Platform Compatibility：待验证。
- Core API Compatibility：待定义。
- Capabilities / Permissions：待定义。
- Release Assets：暂无。
- License and Codec Distribution Review：待审核。

## 相关文档

- [ADR 0068: ECL Media Engine and Standard Media Format Conversion Architecture](../../adr/0068-ecl-media-engine-and-format-conversion-architecture.md)
- [ADR 0032：原生功能入口与按需 Additions 扩展体系](../../adr/0032-native-feature-interfaces-and-optional-additions.md)
- [ADR 0053：Additions 能力权限模型与用户授权流程](../../adr/0053-addition-capability-permission-and-user-consent.md)
- [ADR 0054：Addition 注册表、清单与生命周期治理](../../adr/0054-addition-registry-and-manifest-governance.md)
- [ADR 0055：ECL 混合工作区与原生工具平台架构](../../adr/0055-ecl-hybrid-workspace-and-native-tool-platform-architecture.md)
- [ADR 0067：ECL Music Player and Minecraft Audio Bridge Architecture](../../adr/0067-ecl-music-player-and-minecraft-audio-bridge-architecture.md)

## 修订记录

- 2026-11-09：建立 Proposed 登记条目；明确普通图片/音频/视频格式处理、可替换后端、按需能力交付、许可证审查及平台保护措施绕过边界。无正式发布资产。
