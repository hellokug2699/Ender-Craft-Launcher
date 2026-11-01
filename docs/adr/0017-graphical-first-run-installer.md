# ADR 0017：图形化首次安装程序

- Status: In Progress
- Date: 2026-11-01
- Scope: Windows 首次安装、安装体验、安装介质与 Bootstrapper 集成

## 背景

ECL 当前已有独立的 `ECL.Bootstrapper.exe` 和 `ECL.Deployment` 部署边界，但 Bootstrapper 是控制台程序，面向部署自动化与启动生命周期，不适合作为普通用户首次安装时的交互入口。首次安装需要可发现的 GUI、明确的安装位置和可理解的失败反馈。

安装器必须明确区分程序安装位置与 Minecraft 工作区。用户不应因为安装 ECL 而被迫把大型实例、模组、资源包和游戏文件放入系统盘、`ProgramData` 或隐藏的应用数据目录。

## 决策

1. 新增独立 WPF 项目 `ECL.Installer`，不把首次安装 UI 塞进主启动器或 Bootstrapper。
2. 安装器沿用 ECL 的 HarmonyOS Sans SC 字体、应用图标、深色底色、紫色强调色与卡片式布局。
3. 安装器负责用户交互、程序安装路径选择、安装介质完整性检查、部署进程启动和结果展示；不自行实现第二套归档解包、Manifest 解析或签名校验。
4. 实际部署委托给 `ECL.Bootstrapper.exe`，后者复用 `ECL.Deployment` 中的安全解包、文件验证、版本目录和原子 current 指针切换。
5. **程序安装目录与用户数据、Minecraft 工作区完全分离。** 普通用户安装默认使用当前用户可写的 `%LOCALAPPDATA%\Programs\Ender Craft Launcher`，不要求管理员权限。不得把 Minecraft 实例放入程序安装目录或 `ProgramData`。
6. ECL 的配置、账户凭据、日志和应用状态存放于当前用户的应用数据区域；Java 运行时使用独立的受管理运行时目录。具体路径策略由 [ADR 0018：Minecraft 工作区与实例存储位置](0018-minecraft-workspace-and-instance-storage.md) 约束。
7. Minecraft 工作区由用户决定。安装器不得擅自创建、初始化、迁移或清理实例目录；首次启动时由 ECL 引导用户选择工作区，遵循 ADR 0018 的默认路径与回退规则。
8. 安装器 GUI 不得成为部署逻辑依赖。后续应提供可脚本化的静默安装参数，并让 GUI 与静默模式共用同一安装服务。
9. 安装介质必须携带 Bootstrapper、Manifest、detached signature、发布包和受信任公钥文件。不得从用户可写配置中临时信任任意公钥；不得把签名私钥打包进安装器。
10. 安装器只安装程序文件，不清理实例、账户、配置、日志或 Minecraft 游戏数据。卸载程序也必须将程序移除与用户数据清理分离，并对任何数据删除提供独立、明确的确认。

## 安装介质协议（当前 MVP）

安装器目录旁需要以下文件：

- `ECL.Bootstrapper.exe`
- `update-trust.json`
- `update-manifest.json`
- `update-manifest.sig`
- `ECL-package.zip`

安装器复制 Bootstrapper 与公开信任库到目标程序安装根目录，再通过稳定 CLI 参数启动独立部署。Manifest、签名和包只作为部署输入；签名验证必须在部署端执行。安装介质不包含用户 Minecraft 工作区内容。

## 非目标

- 本 ADR 不将 Bootstrapper 改成 WPF，也不改变其 CLI 退出码契约。
- 本 ADR 不在安装器中实现更新频道 UI、Minecraft 实例迁移、Java 安装或卸载器。
- 本 ADR 不规定 Minecraft 工作区的完整路径、多个实例根目录管理和迁移协议；这些由 ADR 0018 定义。
- 本 ADR 不宣称已完成线上 Release 下载、自动更新、静默安装、Authenticode 签名或完整安装包流水线。

## 后果

- 首次安装的 UI 与部署引擎解耦，后续可独立迭代视觉与交互。
- 程序安装与 Minecraft 数据生命周期分离，避免安装、更新、回滚或卸载意外影响大型用户数据。
- 安装器依赖一套明确的安装介质协议；发布流水线需要产出并打包对应 sidecar。
- 首次安装和应用内更新最终进入同一版本目录布局，减少两套部署状态造成的不一致。

## 验收标准

1. 安装器以无控制台 WPF 应用启动，默认程序安装路径位于当前用户 LocalAppData。
2. 安装器在启动部署前检查所有必需安装介质文件。
3. Bootstrapper 是唯一负责签名验证、归档解包、文件校验和版本指针切换的组件。
4. 部署失败时保留可诊断错误，不破坏已有 current 指针。
5. 安装、更新、回滚和卸载流程均不擅自创建、移动或删除 Minecraft 工作区及用户数据。
6. 首次启动工作区引导遵循 ADR 0018，默认位置可见、可更改，不默认落入 `ProgramData` 或隐藏的应用数据目录。
7. 安装器构建、GUI 启动、有效介质安装和损坏签名拒绝均有 CI/集成测试覆盖。
